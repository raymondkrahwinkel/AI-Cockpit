using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Plugins;

// AC-1724: one plugin's view of the shared `ICockpitActions`, carrying the host-stamped plugin id the shared surface
// has no way to know. Everything forwards, except the prompt into a session, which needs that id on its consent card.
public sealed class PluginScopedActions(string pluginId, string pluginName, ICockpitActions shared, IServiceProvider services) : ICockpitActions
{
    public Task<string> StartSessionAsync(string profileLabel, string? prompt = null, string? workingDirectory = null) =>
        shared.StartSessionAsync(profileLabel, prompt, workingDirectory);

    public Task<string> StartSessionAsync(string profileLabel, string? prompt, string? workingDirectory, string? sessionName) =>
        shared.StartSessionAsync(profileLabel, prompt, workingDirectory, sessionName);

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
