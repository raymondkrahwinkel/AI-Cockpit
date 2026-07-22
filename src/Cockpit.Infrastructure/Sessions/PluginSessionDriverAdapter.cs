using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Wraps a plugin's narrow <see cref="IPluginSessionDriver"/> to satisfy the app's real <see cref="ISessionDriver"/>
/// contract (#45) — the seam that lets <see cref="SessionDriverFactory"/> hand a plugin-backed session to the
/// rest of the app unchanged. The Claude-CLI-only live-control members (permission mode / model / thinking-budget
/// switch, always-allow rule persistence) have no equivalent in the narrow interface and are deliberate no-ops
/// here, gated off in the UI by <see cref="Capabilities"/> reporting them unsupported.
/// </summary>
internal sealed class PluginSessionDriverAdapter(IPluginSessionDriver inner, PluginSessionCapabilities pluginCapabilities, McpAuthKey authKey, IMcpServerCatalog? mcpServerCatalog = null, ILogger<PluginSessionDriverAdapter>? logger = null, SessionMcpKeyring? keyring = null) : ISessionDriver
{
    // Live model switch / plan mode / thinking budget have no equivalent on the narrow IPluginSessionDriver
    // surface (no members could back them — see PluginSessionCapabilities) — always unsupported here rather
    // than a flag a plugin could set true with nothing behind it (#45 review finding 3). SupportsVision is
    // mapped straight through instead of forced false: every built-in example plugin already reports it
    // false (IPluginSessionDriver.SendUserMessageAsync has no images parameter yet, #64 fase 2), so this
    // stays honest without another host-side change once that surface can actually carry images.
    // Live model switch and permission-mode switch are now mapped straight through (Fase 4 D4): the narrow surface can
    // back them via SetLiveOptionAsync, which SetModelAsync/SetPermissionModeAsync below are wired to, so a plugin that
    // declares it (the Claude provider) drives the host's native model/permission dropdowns. Plan mode and thinking
    // budget still have no equivalent on the narrow surface and stay false.
    public SessionCapabilities Capabilities { get; } = new(
        SupportsTools: pluginCapabilities.SupportsTools,
        SupportsPermissions: pluginCapabilities.SupportsPermissions,
        SupportsLiveModelSwitch: pluginCapabilities.SupportsLiveModelSwitch,
        SupportsPlanMode: false,
        SupportsThinking: false,
        SupportsVision: pluginCapabilities.SupportsVision,
        SupportsPermissionModeSwitch: pluginCapabilities.SupportsPermissionModeSwitch)
    {
        SupportsEnvVars = pluginCapabilities.SupportsEnvVars,
        ConfinesFileAccessToWorkingDirectory = pluginCapabilities.ConfinesFileAccessToWorkingDirectory,
    };

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
        [.. inner.LiveOptions.Select(option => new SessionLiveOption(option.Key, option.Label, option.Choices, option.DefaultValue) { ChoiceLabels = option.ChoiceLabels })];

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

        // #44/AC-130: a launch that carries no per-session selection (a programmatic open — a plugin/workflow
        // shortcut, a restored session — rather than the New-session dialog, which builds one from the checklist)
        // still honours the profile's saved MCP selection instead of silently reaching every enabled server.
        // Programmatic launches only ever take this SDK route (StartSessionForPluginAsync always starts an SDK
        // session), so the fallback belongs here rather than on the dialog-only TTY route.
        var selection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);
        var mcpServers = await _ResolveMcpServersAsync(selection, cancellationToken).ConfigureAwait(false);

        // The host carries the operator's permission-mode selection as a typed parameter (a Claude concept older than
        // the plugin surface, which has no such parameter). Fold it into the options map under the well-known key so a
        // provider that declared a permission-mode option actually receives the choice — without it, a Claude plugin
        // session always fell back to the driver's own default (e.g. an operator's launch-time "bypassPermissions"
        // silently became "default"). The operator's explicit choice in the launch options wins; the typed value only
        // fills the key when the launch options carry none (see _MergePermissionMode) — folding it over an explicit
        // choice is what let a profile's stale default run a write tool ungated.
        var options = _MergePermissionMode(launchOptions, permissionMode);
        var environment = _SpawnEnvironment(profile, launchOptions);
        await inner.StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, environment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The environment the plugin driver receives: this run's MCP auth key (AC-40) so a cockpit-hosted server's
    /// config can reference it instead of embedding a literal, plus the profile's own variables (AC-22) scrubbed
    /// host-side — a variable on a host-controlled key (an <c>ANTHROPIC_*</c> credential, a nested-agent marker) is
    /// dropped here, the same rule the TTY route applies, so no plugin has to be trusted to apply it. Dropping is
    /// logged by name, never by value.
    /// </summary>
    private IReadOnlyDictionary<string, string> _SpawnEnvironment(SessionProfile? profile, IReadOnlyDictionary<string, string>? launchOptions)
    {
        // AC-89: hand a session that has a pane id (the App passes it as the cockpit.pane-id launch option) its own
        // per-session token as COCKPIT_MCP_KEY instead of the shared app key, so the consent broker can attribute a
        // request to the real session rather than trust the id the agent declares. No pane id (or no keyring in a test
        // graph) falls back to the shared key.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var value) ? value : null;
        var mcpKey = keyring is not null && !string.IsNullOrEmpty(paneId) ? keyring.TokenFor(paneId) : authKey.Value;

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WellKnownSessionEnvironment.CockpitMcpKey] = mcpKey,
        };

        if (profile?.EnvironmentVariables is not { Count: > 0 } variables)
        {
            return environment;
        }

        var rejected = new List<string>();
        foreach (var variable in variables)
        {
            if (TtyEnvironment.IsHostControlled(variable.Key))
            {
                rejected.Add(variable.Key);
                continue;
            }

            environment[variable.Key] = variable.Value;
        }

        if (rejected.Count > 0)
        {
            logger?.LogWarning(
                "Profile {Profile} configures host-controlled environment variables; ignored: {Variables}",
                profile.Label,
                string.Join(", ", rejected));
        }

        return environment;
    }

    private static IReadOnlyDictionary<string, string>? _MergePermissionMode(IReadOnlyDictionary<string, string>? launchOptions, string? permissionMode)
    {
        if (string.IsNullOrWhiteSpace(permissionMode))
        {
            return launchOptions;
        }

        var merged = launchOptions is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(launchOptions);

        // The operator's explicit choice in the provider's own permission-mode launch option wins; the host's typed
        // fold only supplies one when the options carry none (a route with no permission-mode option of its own). Without
        // this, a profile's stale typed default silently overrode a launch-time change in the generic dropdown — a
        // session started with "Ask permissions" ran a write tool ungated because bypass folded over it.
        if (!merged.ContainsKey(WellKnownPluginSessionOptions.PermissionMode))
        {
            merged[WellKnownPluginSessionOptions.PermissionMode] = permissionMode;
        }

        return merged;
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
        if (mcpServerCatalog is null)
        {
            return [];
        }

        try
        {
            var registry = await mcpServerCatalog.GetServersAsync(cancellationToken).ConfigureAwait(false);
            var servers = McpServerRegistryFilter.ApplySessionSelection(registry, enabledServerNames)
                .Where(McpConfigFile.IsAgentEligible)
                .Select(_ToPluginMcpServer)
                .OfType<PluginMcpServer>()
                .ToList();

            // Say what the session got and against which selection, so the next "why are my MCP servers missing?"
            // is a log line, not a bisect (#44). A non-empty selection that resolves to nothing is almost always a
            // wiring slip (a saved name the registry no longer has, or one filtered out as not agent-eligible), so
            // surface that case at Warning; the ordinary fan-out stays at Information.
            var selectionText = enabledServerNames is null ? "(no restriction)" : $"[{string.Join(", ", enabledServerNames)}]";
            if (servers.Count == 0 && enabledServerNames is { Count: > 0 })
            {
                logger?.LogWarning(
                    "Session MCP fan-out resolved no servers from selection {Selection}; the session starts with none.",
                    selectionText);
            }
            else
            {
                logger?.LogInformation(
                    "Session MCP fan-out: {Count} server(s) [{Names}] from selection {Selection}.",
                    servers.Count,
                    string.Join(", ", servers.Select(server => server.Name)),
                    selectionText);
            }

            return servers;
        }
        catch (Exception exception)
        {
            // Best-effort: a transient registry read failure launches the session without the shared servers rather
            // than failing the whole start — but no longer silently, since "started with zero servers" read as
            // "chose zero servers" is exactly what made this hard to see.
            logger?.LogWarning(exception, "Resolving the session's MCP servers failed; the session starts with none.");
            return [];
        }
    }

    // HTTP → url with the user API-key server's own bearer, plus a CockpitHosted flag for a cockpit loopback endpoint
    // (whose auth rides the COCKPIT_MCP_KEY env var, not a literal here — AC-40); stdio → command/args. A server
    // missing its transport target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server) => server.Transport switch
    {
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserApiKey(server),
            CockpitHosted = server.CockpitHosted,
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
        inner.SendUserMessageAsync(
            text,
            images?.Select(image => new PluginImageAttachment(image.MediaType, image.Base64Data)).ToList(),
            cancellationToken);

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
    // The host's native permission-mode / model dropdowns switch mid-session through these; wire them to the plugin's
    // generic live-option surface under the well-known keys (Fase 4 D4). A plugin that does not declare the matching
    // SupportsLiveModelSwitch / SupportsPermissionModeSwitch capability never has the host call these, and one that
    // declares no such live option no-ops it in SetLiveOptionAsync — so this is safe for every plugin.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.PermissionMode, mode, cancellationToken);

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.Model, model ?? string.Empty, cancellationToken);

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
