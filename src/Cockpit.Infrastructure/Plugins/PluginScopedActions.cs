using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// The plugin's view of ICockpitActions, with the identity needed for consent and session ownership.
public sealed class PluginScopedActions(string pluginId, string pluginName, ICockpitActions shared, IServiceProvider services) : ICockpitActions
{
    public Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        shared.StartSessionAsync(profileLabel, prompt, workingDirectory);

    public Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName) =>
        shared.StartSessionAsync(profileLabel, prompt, workingDirectory, sessionName);

    public async Task<PluginSessionStartResult> StartSessionAsync(PluginSessionStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var launcher = _LocalLauncher();
        var gateway = services.GetService<IAssistantAgentGateway>()
            ?? throw new NotSupportedException("This host cannot start plugin-owned sessions.");

        var deskId = request.DeskId ?? await launcher.RunExclusiveAsync(() =>
            launcher.Workspaces.Active is { } active && active.Type == WorkspaceType.Sessions
                ? active.Id
                : launcher.Workspaces.Workspaces.FirstOrDefault(desk => desk.Type == WorkspaceType.Sessions)?.Id).ConfigureAwait(false);
        Dictionary<string, string> options = [];
        if (request.Model is { } model)
        {
            options.Add(WellKnownPluginSessionOptions.Model, model);
        }
        if (request.Effort is { } effort)
        {
            options.Add(WellKnownPluginSessionOptions.Effort, effort);
        }

        var result = await gateway.SpawnAsync(new AgentSpawnRequest(
            SpawnTarget.NamedByAPlugin(deskId ?? string.Empty, pluginId), request.ProfileLabel, request.ProjectId,
            request.Prompt, request.WorkingDirectory, request.SessionName, OptionOverrides: options,
            IsolateInWorktree: request.IsolateInWorktree, McpServers: request.McpServers?.ToArray(),
            Role: request.Role, RoleChoiceRequired: true)).ConfigureAwait(false);

        return new(result.Ok, result.PaneId, result.SessionName, result.ResolvedProfileLabel,
            result.PromptDelivered, result.McpServers, result.Error, result.McpServersConfirmed);
    }

    public Task<bool> CloseSessionAsync(string paneId) => _LocalLauncher().ClosePluginSessionAsync(paneId, pluginId);

    public Task<PluginSessionMcpResult> AddSessionMcpServerAsync(string paneId, string serverName) =>
        _McpGateway().AddPluginSessionMcpServerAsync(pluginId, _LocalLauncher().FindPluginSession(paneId, pluginId), serverName);

    public Task<PluginSessionMcpResult> GetSessionMcpSetAsync(string paneId) =>
        _McpGateway().ReadPluginSessionMcpSetAsync(_LocalLauncher().FindPluginSession(paneId, pluginId));

    public Task<IReadOnlyList<PluginSessionMcpServerCheck>> CheckSessionMcpServersAsync(
        string? profileLabel, string? projectId, IReadOnlyList<string> serverNames)
    {
        _LocalLauncher();
        ArgumentNullException.ThrowIfNull(serverNames);
        return _McpGateway().CheckPluginMcpServersAsync(pluginId, profileLabel, projectId, serverNames.ToArray());
    }

    private AssistantAgentGateway _McpGateway() =>
        services.GetService<IAssistantAgentGateway>() as AssistantAgentGateway
        ?? throw new NotSupportedException("This host cannot manage MCP servers for plugin-owned sessions.");

    private SessionLauncher _LocalLauncher() =>
        services.GetService<ISessionLauncher>() as SessionLauncher
        ?? throw new NotSupportedException("Plugin-owned sessions are supported on the host running the plugin backend.");

    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory = null, TimeSpan? timeout = null) =>
        shared.DelegateAsync(profileLabel, prompt, workingDirectory, timeout);

    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory, TimeSpan? timeout, string? permission) =>
        shared.DelegateAsync(profileLabel, prompt, workingDirectory, timeout, permission);

    public Task<string> DelegateAsync(string profileLabel, string prompt, string? workingDirectory, TimeSpan? timeout, string? permission, Action<string> taskStarted) =>
        shared.DelegateAsync(profileLabel, prompt, workingDirectory, timeout, permission, taskStarted);

    // The same road as the assistant's send_prompt: a Dangerous card that is never remembered, then the gateway, whose
    // refusals (the assistant's pane, a pane with no agent) and hold-until-ready stand. The card sits on the target pane.
    public async Task SendPromptToSessionAsync(string paneId, string prompt, string reason)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A turn needs something to say; that prompt was empty.", nameof(prompt));
        }

        if (string.Equals(paneId, AssistantIdentity.PaneId, StringComparison.Ordinal)
            || services.GetService<ISessionRegistry>()?.Find(paneId) is not { } session)
        {
            throw new InvalidOperationException($"No agent session has pane id '{paneId}'.");
        }

        if (services.GetService<IConsentBroker>() is not { } consent || services.GetService<IAssistantAgentGateway>() is not { } gateway)
        {
            throw new NotSupportedException("This host has nobody to ask, so it cannot send a prompt to a session.");
        }

        var decision = await consent.RequestConsentAsync(PromptCard(pluginId, pluginName, paneId, session.Title, prompt, reason))
            .ConfigureAwait(false);
        if (decision.IsApproved is false)
        {
            throw new InvalidOperationException("Declined by the operator.");
        }

        var result = await gateway.SendPromptAsync(paneId, prompt).ConfigureAwait(false);
        if (result.Ok is false)
        {
            throw new InvalidOperationException(result.Error);
        }
    }

    // The card the operator answers, public so the screenshot scene renders this one and not a copy. The scope
    // carries a hash of the text, so an audit line can be matched to a prompt without the action text alone.
    public static ConsentRequest PromptCard(string pluginId, string pluginName, string paneId, string sessionTitle, string prompt, string reason) =>
        new(
            $"{pluginName} wants to submit a turn in another session",
            $"Session: {_OneLine(sessionTitle)} ({paneId})\nReason: {_OneLine(reason)}\n\n{prompt}",
            new ConsentSource(paneId, pluginId, ConsentSourceCatalog.PluginPrompt),
            $"plugin.prompt sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)))}",
            ConsentRisk.Dangerous);

    // A plugin-chosen reason or name must not draw extra lines on the card, such as a forged "Session:" line.
    private static string _OneLine(string? text) =>
        string.Concat((text ?? string.Empty).Select(character => char.IsControl(character) ? ' ' : character)).Trim();
}
