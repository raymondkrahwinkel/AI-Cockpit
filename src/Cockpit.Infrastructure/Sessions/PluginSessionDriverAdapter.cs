using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Wraps a plugin's narrow <see cref="IPluginSessionDriver"/> to satisfy the app's real <see cref="ISessionDriver"/>
/// contract (#45) — the seam that lets <see cref="SessionDriverFactory"/> hand a plugin-backed session to the
/// rest of the app unchanged. The Claude-CLI-only live-control members (permission mode / model / thinking-budget
/// switch, always-allow rule persistence) have no equivalent in the narrow interface and are deliberate no-ops
/// here, gated off in the UI by <see cref="Capabilities"/> reporting them unsupported.
/// </summary>
internal sealed class PluginSessionDriverAdapter(IPluginSessionDriver inner, PluginSessionCapabilities pluginCapabilities, IMcpServerStore? mcpServerStore = null) : ISessionDriver
{
    // Live model switch / plan mode / thinking budget have no equivalent on the narrow IPluginSessionDriver
    // surface (no members could back them — see PluginSessionCapabilities) — always unsupported here rather
    // than a flag a plugin could set true with nothing behind it (#45 review finding 3). SupportsVision is
    // mapped straight through instead of forced false: every built-in example plugin already reports it
    // false (IPluginSessionDriver.SendUserMessageAsync has no images parameter yet, #64 fase 2), so this
    // stays honest without another host-side change once that surface can actually carry images.
    public SessionCapabilities Capabilities { get; } = new(
        SupportsTools: pluginCapabilities.SupportsTools,
        SupportsPermissions: pluginCapabilities.SupportsPermissions,
        SupportsLiveModelSwitch: false,
        SupportsPlanMode: false,
        SupportsThinking: false,
        SupportsVision: pluginCapabilities.SupportsVision);

    public string? SessionId => inner.SessionId;

    // The plugin driver's process, when it spawns one (Codex app-server), so the host's resource meter has
    // something to weigh (#78, D10) — null for an HTTP-backed provider, same as the ISessionDriver default.
    public int? ProcessId => inner.ProcessId;

    public SessionProfile? Profile { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _AdaptEventsAsync();

    // The plugin driver reports its status as a provider-neutral snapshot (#45 D7); map it to the core model the
    // header renders — each window carried through with the label the provider chose, so the host imposes no
    // window vocabulary. The Claude-CLI-only live-control no-ops above have no state to poll; this one does,
    // because a plugin provider (Codex) genuinely reports usage the narrow surface can carry.
    public SessionStatusFeed? CurrentStatus => _MapStatus(inner.Status);

    private static SessionStatusFeed? _MapStatus(PluginSessionStatus? status) =>
        status is { HasAny: true }
            ? new SessionStatusFeed(
                status.ContextUsedPercent,
                [.. status.RateLimits.Select(window => new SessionRateWindow(window.Label, window.UsedPercent, window.ResetsAt))])
            : null;

    // The plugin driver's mid-session controls (#45 D4), mapped to the core form the header's live-control panel
    // renders. Unlike the Claude-CLI live switches below (no-ops here — the narrow surface has no typed members for
    // them), a plugin provider genuinely reports these and answers SetLiveOptionAsync, so they carry through.
    public IReadOnlyList<SessionLiveOption> LiveOptions =>
        [.. inner.LiveOptions.Select(option => new SessionLiveOption(option.Key, option.Label, option.Choices, option.DefaultValue))];

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(key, value, cancellationToken);

    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, CancellationToken cancellationToken = default)
    {
        // workingDirectory, resume, launchOptions and the session's MCP servers are passed through (#45 D5, #44):
        // a plugin driver that spawns a CLI (Codex app-server) runs in a cwd, resumes a thread by id, honours the
        // operator's answers to the options it declared (sandbox, model), and exposes the registry servers the
        // operator selected. Dropping them here is what made the Codex plugin ask for a working directory the
        // cockpit already had, left its sandbox/model unreachable per session, and reported "Connected (0 tools)".
        // A driver with no cwd/history/options/tool source of its own (an HTTP provider) simply ignores them. Only
        // BySessionId resume crosses the narrow surface; MostRecent needs a provider-side "list newest" step (increment 2).
        Profile = profile;
        var resumeSessionId = resume is { Mode: SessionResumeMode.BySessionId, SessionId: { Length: > 0 } sessionId } ? sessionId : null;
        var mcpServers = await _ResolveMcpServersAsync(enabledMcpServerNames, cancellationToken).ConfigureAwait(false);
        await inner.StartAsync(model, workingDirectory, resumeSessionId, launchOptions, mcpServers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns the operator's per-session MCP selection into the concrete endpoints the plugin driver exposes:
    /// the shared per-session narrowing (<see cref="McpServerRegistryFilter.ApplySessionSelection"/>, the same
    /// one <c>ClaudeCliProcess</c> and the local-model tool-loop apply) intersected with the agent-eligible
    /// servers (<see cref="McpConfigFile.IsAgentEligible"/>). The registry lives host-side (plugin isolation
    /// keeps it out of the driver), so the adapter resolves names to definitions here. No store (a unit test
    /// that does not wire one) means no fan-out. Best-effort — a transient <c>cockpit.json</c> read failure
    /// launches the session without the shared servers rather than failing the whole start, matching how the
    /// Claude fan-out treats the same read.
    /// </summary>
    private async Task<IReadOnlyList<PluginMcpServer>> _ResolveMcpServersAsync(IReadOnlySet<string>? enabledServerNames, CancellationToken cancellationToken)
    {
        if (mcpServerStore is null)
        {
            return [];
        }

        try
        {
            var registry = await mcpServerStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return McpServerRegistryFilter.ApplySessionSelection(registry, enabledServerNames)
                .Where(McpConfigFile.IsAgentEligible)
                .Select(_ToPluginMcpServer)
                .OfType<PluginMcpServer>()
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Mirrors McpConfigFile's Claude mapping: HTTP → url with a static bearer token for an API-key server (the
    // driver keeps it off the command line), stdio → command/args. A server missing its transport target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = server.Auth == McpServerAuth.ApiKey && !string.IsNullOrWhiteSpace(server.ApiKey) ? server.ApiKey : null,
        },
        McpTransport.Stdio when !string.IsNullOrWhiteSpace(server.Command) => new PluginMcpServer
        {
            Name = server.Name,
            Command = server.Command,
            Args = server.Args,
        },
        _ => null,
    };

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        inner.SendUserMessageAsync(text, cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        inner.InterruptAsync(cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        inner.RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default) =>
        inner.SetAutoApproveToolsAsync(enabled, cancellationToken);

    // Always-allow is session-scoped on the narrow plugin surface (D4): forward the intent so a driver that can
    // persist it for the session (Codex's acceptForSession) does, and one that cannot falls back to a one-time
    // allow via the interface default. The Claude rule args (toolName/input/scope) have no equivalent here — a
    // cross-restart per-profile rule stays a Claude-CLI concern, which is why they are not passed on.
    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) =>
        inner.AllowPermissionAlwaysAsync(toolUseId, cancellationToken);

    // No live control channel behind the narrow interface — these Claude-CLI-only operations are deliberate no-ops.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private async IAsyncEnumerable<SessionEvent> _AdaptEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var pluginEvent in inner.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return _Adapt(pluginEvent);
        }
    }

    private static SessionEvent _Adapt(PluginSessionEvent pluginEvent) => pluginEvent switch
    {
        PluginSessionInitialized initialized => new SessionInitialized
        {
            SessionId = initialized.SessionId,
            Cwd = initialized.Cwd ?? string.Empty,
            Tools = initialized.Tools,
        },
        PluginAssistantThinkingDelta thinking => new AssistantThinkingDelta
        {
            SessionId = thinking.SessionId,
            BlockIndex = thinking.BlockIndex,
            Thinking = thinking.Thinking,
        },
        PluginAssistantTextDelta delta => new AssistantTextDelta
        {
            SessionId = delta.SessionId,
            BlockIndex = delta.BlockIndex,
            Text = delta.Text,
        },
        PluginToolUseRequested toolUse => new ToolUseRequested
        {
            SessionId = toolUse.SessionId,
            ToolUseId = toolUse.ToolUseId,
            ToolName = toolUse.ToolName,
            InputJson = toolUse.InputJson,
        },
        PluginToolResult toolResult => new ToolResult
        {
            SessionId = toolResult.SessionId,
            ToolUseId = toolResult.ToolUseId,
            Content = toolResult.Content,
            IsError = toolResult.IsError,
        },
        PluginPermissionRequested permission => new PermissionRequested
        {
            SessionId = permission.SessionId,
            ToolUseId = permission.ToolUseId,
            ToolName = permission.ToolName,
            InputJson = permission.InputJson,
        },
        PluginTurnCompleted turnCompleted => new TurnCompleted
        {
            SessionId = turnCompleted.SessionId,
            Subtype = turnCompleted.Subtype,
            Result = turnCompleted.Result,
            IsError = turnCompleted.IsError,
            StopReason = turnCompleted.StopReason,
            Usage = turnCompleted.Usage is { } usage
                ? new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens)
                : null,
            TotalCostUsd = turnCompleted.TotalCostUsd,
            NumTurns = turnCompleted.NumTurns,
        },
        PluginSessionError error => new SessionError
        {
            SessionId = error.SessionId,
            Message = error.Message,
        },
        _ => new UnknownEvent { SessionId = pluginEvent.SessionId, RawJson = string.Empty },
    };
}
