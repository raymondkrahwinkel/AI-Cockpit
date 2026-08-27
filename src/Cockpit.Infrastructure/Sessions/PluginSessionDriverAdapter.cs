using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Sessions.Tty;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Wraps a plugin's narrow `IPluginSessionDriver` to satisfy the app's real `ISessionDriver`
// contract (#45), the seam `SessionDriverFactory` uses. Claude-CLI-only live-control members
// (permission mode/model/thinking-budget switch, always-allow persistence) are deliberate no-ops here.
internal sealed class PluginSessionDriverAdapter(IPluginSessionDriver inner, PluginSessionCapabilities pluginCapabilities, McpAuthKey authKey, IMcpServerCatalog? mcpServerCatalog = null, ILogger<PluginSessionDriverAdapter>? logger = null, SessionMcpKeyring? keyring = null, ISessionResourceResolver? sessionResources = null, IMcpOAuthCoordinator? oauthCoordinator = null, ISessionConversationSink? conversationSink = null, IMcpOAuthProxy? oauthProxy = null, IWorktreeManager? worktreeManager = null, SessionMcpMounts? mcpMounts = null, IMcpToolProvider? mcpToolProvider = null) : ISessionDriver
{
    // #45 review finding 3: plan mode/thinking budget have no equivalent on the narrow surface, so always
    // unsupported here. SupportsVision maps straight through (every built-in plugin reports it false today, #64 fase 2).

    // AC-190: permission modes that keep a permission-based provider's file-access guard engaged. An allowlist
    // (bypassPermissions and any unrecognised mode are not-confining) so a future mode fails closed until reviewed.
    private static readonly IReadOnlySet<string> PermissionEngagedModes =
        new HashSet<string>(StringComparer.Ordinal) { "default", "acceptEdits", "plan" };

    // The mode assumed when a session carries no explicit permission-mode selection — the Claude driver's own default,
    // which keeps the permission system engaged (and so confines).
    private const string DefaultPermissionMode = "default";

    // Null until StartAsync resolves it; a permission-based provider then reads null as "not yet confirmed"
    // and reports unconfined, so it fails closed before start rather than on an assumption.
    private bool? _permissionModeConfines;

    // Fase 4 D4: model/permission-mode switch map through SetLiveOptionAsync below. AC-190:
    // ConfinesFileAccessToWorkingDirectory recomputes on each read, not at construction, since a bypass
    // permission mode disables a permission-based provider's guard. AC-964: SupportsTools also takes the running driver's answer.
    public SessionCapabilities Capabilities => new(
        SupportsTools: pluginCapabilities.SupportsTools || inner.Capabilities.SupportsTools,
        SupportsPermissions: pluginCapabilities.SupportsPermissions,
        SupportsLiveModelSwitch: pluginCapabilities.SupportsLiveModelSwitch,
        SupportsPlanMode: false,
        SupportsThinking: false,
        SupportsVision: pluginCapabilities.SupportsVision,
        SupportsPermissionModeSwitch: pluginCapabilities.SupportsPermissionModeSwitch)
    {
        SupportsEnvVars = pluginCapabilities.SupportsEnvVars,
        ConfinesFileAccessToWorkingDirectory = _EffectiveConfinesFileAccessToWorkingDirectory(),
        SupportsContextCompaction = pluginCapabilities.SupportsContextCompaction,
        SupportsMidTurnInput = pluginCapabilities.SupportsMidTurnInput,
    };

    // AC-190: a provider with no confinement vouch never confines; a real-sandbox provider (not
    // ConfinesViaPermissionsOnly) confines unconditionally; a permission-based one only once its mode is confirmed engaged (fail closed otherwise).
    private bool _EffectiveConfinesFileAccessToWorkingDirectory()
    {
        if (!pluginCapabilities.ConfinesFileAccessToWorkingDirectory)
        {
            return false;
        }

        if (!pluginCapabilities.ConfinesViaPermissionsOnly)
        {
            return true;
        }

        return _permissionModeConfines ?? false;
    }

    public string? SessionId => inner.SessionId;

    // The plugin driver's process, when it spawns one (Codex app-server), so the host's resource meter has
    // something to weigh (#78, D10) — null for an HTTP-backed provider, same as the ISessionDriver default.
    public int? ProcessId => inner.ProcessId;

    public SessionProfile? Profile { get; private set; }

    public IAsyncEnumerable<SessionEvent> Events => _AdaptEventsAsync();

    // #45 D7: maps the plugin's provider-neutral status snapshot to the core model the header renders,
    // each window kept under the label the provider chose (the host imposes no window vocabulary).
    public SessionStatusFeed? CurrentStatus => _MapStatus(inner.Status);

    private static SessionStatusFeed? _MapStatus(PluginSessionStatus? status) =>
        status is { HasAny: true }
            ? new SessionStatusFeed(
                status.ContextUsedPercent,
                [.. status.RateLimits.Select(window => new SessionRateWindow(window.Label, window.UsedPercent, window.ResetsAt))])
            : null;

    // #45 D4: mid-session controls mapped to the core form the header's live-control panel renders.
    // Unlike the Claude-CLI switches below (no-ops), a plugin genuinely reports and answers these.
    public IReadOnlyList<SessionLiveOption> LiveOptions =>
        [.. inner.LiveOptions.Select(option => new SessionLiveOption(option.Key, option.Label, option.Choices, option.DefaultValue) { ChoiceLabels = option.ChoiceLabels })];

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(key, value, cancellationToken);

    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
    {
        // #45 D5/#44: workingDirectory, resume, launchOptions and MCP servers pass through — dropping them made
        // the Codex plugin ask for a cwd it already had and report "Connected (0 tools)". Only BySessionId resume
        // crosses the narrow surface; MostRecent needs a provider-side "list newest" step (increment 2).
        Profile = profile;
        var resumeSessionId = resume is { Mode: SessionResumeMode.BySessionId, SessionId: { Length: > 0 } sessionId } ? sessionId : null;

        // #44/AC-130: a launch with no per-session selection (a programmatic open, not the New-session dialog)
        // still honours the profile's saved MCP selection rather than silently reaching every enabled server.
        var selection = McpServerRegistryFilter.EffectiveSessionSelection(enabledMcpServerNames, profile?.EnabledMcpServerNames);

        // AC-165: what the plugins give this session, resolved from the pane it is starting in so a contribution
        // can depend on the project that pane belongs to. AC-408: kept on the field too, so the event loop below
        // knows which pane to report a later conversation-id change against.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var pane) ? pane : null;
        _paneId = paneId;

        // AC-964: a provider that declared a host tool loop gets the tools already connected and gated instead of
        // the endpoints to mount itself — the two are alternatives, so the server list stays empty here rather than
        // handing the same servers over twice.
        _hostToolset = await _ConnectHostToolsetAsync(selection, paneId, workingDirectory, projectId, launchOptions, cancellationToken).ConfigureAwait(false);
        var mcpServers = _hostToolset is null
            ? await _ResolveMcpServersAsync(selection, projectId, workingDirectory, cancellationToken).ConfigureAwait(false)
            : [];

        // AC-927: what this session actually mounted, so the header names those servers rather than the checklist
        // it was launched from — which never holds the always-mounted and auto-mounted ones it also just got.
        if (paneId is { Length: > 0 })
        {
            mcpMounts?.Report(
                paneId,
                _hostToolset is { } toolset ? toolset.ConnectedServerNames : [.. mcpServers.Select(server => server.Name)],
                _hostToolset?.ConnectionIssues);
        }

        var contributed = sessionResources is null
            ? SessionResources.Empty
            : await sessionResources.ResolveAsync(paneId, cancellationToken).ConfigureAwait(false);

        // The host's typed permission-mode parameter (older than the plugin surface) folds into the options
        // map under the well-known key, but only when launch options carry none (see _MergePermissionMode) — else
        // a Claude session's launch-time "bypassPermissions" silently became "default".
        var options = _StateAttendance(_MergePermissionMode(launchOptions, permissionMode));

        // AC-190: resolved from the same effective options so Capabilities reports the real per-session
        // confinement, not the static registration vouch, once the isolation gate checks it post-start.
        _permissionModeConfines = _PermissionModeConfines(options);

        var environment = _SpawnEnvironment(profile, launchOptions, contributed);
        await inner.StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, environment, _hostToolset, cancellationToken).ConfigureAwait(false);
    }

    // AC-964: the host-run tool loop for a provider that asked for one, null otherwise (the default today).
    // Same ConnectAsync call as the local-model driver, so AC-89/AC-174/AC-218 hold identically here.
    private async Task<HostPluginToolset?> _ConnectHostToolsetAsync(
        IReadOnlySet<string>? selection,
        string? paneId,
        string? workingDirectory,
        string? projectId,
        IReadOnlyDictionary<string, string>? launchOptions,
        CancellationToken cancellationToken)
    {
        if (pluginCapabilities.HostToolLoop == PluginHostToolLoop.None || mcpToolProvider is null)
        {
            return null;
        }

        // Confinement (AC-174) needs both the host's flag and a real directory to confine to; the capability the
        // isolation gate reads is vouched from that same pair, so a flag without a directory never confines.
        var confineRoot = launchOptions is not null
            && launchOptions.TryGetValue(WellKnownPluginSessionOptions.ConfineFileToolsToWorkingDirectory, out var confineFlag)
            && string.Equals(confineFlag, "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : null;

        return await HostPluginToolset.ConnectAsync(
            mcpToolProvider,
            pluginCapabilities.HostToolLoop,
            selection,
            paneId,
            confineRoot,
            projectId,
            workingDirectory,
            () => inner.SessionId,
            logger,
            cancellationToken).ConfigureAwait(false);
    }

    // Non-null once StartAsync mounted one for this session.
    private HostPluginToolset? _hostToolset;

    // AC-40: this run's MCP auth key, plus the profile's own variables (AC-22) scrubbed host-side of
    // host-controlled keys (dropped by name, never by value, same rule the TTY route applies).
    // AC-165: plugin contributions go on last so a project's answer beats the profile's default.
    private IReadOnlyDictionary<string, string> _SpawnEnvironment(
        SessionProfile? profile,
        IReadOnlyDictionary<string, string>? launchOptions,
        SessionResources contributed)
    {
        // AC-89: a session with a pane id gets its own per-session token as COCKPIT_MCP_KEY, so the consent
        // broker attributes requests to the real session; no pane id falls back to the shared key.
        // AC-994: reuse a host toolset's already-baked pane token instead of minting a second, invalidating one.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var value) ? value : null;
        var mcpKey = _hostToolset is { PaneToken: { } toolsetToken }
            ? toolsetToken
            : keyring is not null && !string.IsNullOrEmpty(paneId) ? keyring.TokenFor(paneId) : authKey.Value;

        // Remembered so this session's token dies with it (AC-89). Only when we minted one here: the host toolset's
        // token is revoked by its own DisposeAsync, and the shared app key is the app's, not ours to drop.
        if (_hostToolset is null && keyring is not null && !string.IsNullOrEmpty(paneId))
        {
            _minted = new MintedToken(paneId, mcpKey);
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WellKnownSessionEnvironment.CockpitMcpKey] = mcpKey,
        };

        if (profile?.EnvironmentVariables is { Count: > 0 } variables)
        {
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
        }

        // Scrubbed again here rather than trusted to have been: this is where the value is put in the environment the
        // driver receives, and the TTY route's own composition re-checks the same rule at its equivalent point. A
        // guard that holds only because an earlier layer ran is one refactor away from not holding at all.
        foreach (var variable in contributed.EnvironmentVariables.Where(variable => !TtyEnvironment.IsHostControlled(variable.Key)))
        {
            environment[variable.Key] = variable.Value;
        }

        return environment;
    }

    // AC-190: no permission-mode key means the driver falls back to its own (confining) default; an
    // explicit value confines only when on the allowlist — bypassPermissions and unrecognised modes fail closed.
    private static bool _PermissionModeConfines(IReadOnlyDictionary<string, string>? options) =>
        _ModeConfines(options is not null
            && options.TryGetValue(WellKnownPluginSessionOptions.PermissionMode, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null);

    // AC-190: mode is on the engaged allowlist, or absent (driver's own confining default) — fail closed
    // otherwise. Shared by the start-time resolution and the live-switch recompute so both agree.
    private static bool _ModeConfines(string? mode) =>
        PermissionEngagedModes.Contains(string.IsNullOrWhiteSpace(mode) ? DefaultPermissionMode : mode);

    private static IReadOnlyDictionary<string, string>? _MergePermissionMode(IReadOnlyDictionary<string, string>? launchOptions, string? permissionMode)
    {
        if (string.IsNullOrWhiteSpace(permissionMode))
        {
            return launchOptions;
        }

        var merged = launchOptions is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(launchOptions);

        // The operator's explicit choice wins; the host's typed fold only supplies one when the options
        // carry none — else a profile's stale default silently overrode a launch-time dropdown change.
        if (!merged.ContainsKey(WellKnownPluginSessionOptions.PermissionMode))
        {
            merged[WellKnownPluginSessionOptions.PermissionMode] = permissionMode;
        }

        return merged;
    }

    // AC-378: states on every launch whether an operator is watching, explicitly, since a driver reading
    // nothing must assume the safe (unattended) answer — a driver assuming the other way would hand a
    // delegated agent the operator's own connectors on a host too old to state this.
    private static IReadOnlyDictionary<string, string>? _StateAttendance(IReadOnlyDictionary<string, string>? options)
    {
        if (options is not null && options.ContainsKey(WellKnownPluginSessionOptions.Unattended))
        {
            return options;
        }

        var stated = options is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
        stated[WellKnownPluginSessionOptions.Unattended] = "false";
        return stated;
    }

    // Turns the operator's per-session MCP selection into concrete endpoints: the shared narrowing
    // (`McpServerRegistryFilter.ApplySessionSelection`) intersected with agent-eligible servers. Registry lives
    // host-side (plugin isolation), so the adapter resolves names here. Best-effort on a transient read failure. AC-218 scopes projectId.
    private async Task<IReadOnlyList<PluginMcpServer>> _ResolveMcpServersAsync(IReadOnlySet<string>? enabledServerNames, string? projectId, string? workingDirectory, CancellationToken cancellationToken)
    {
        if (mcpServerCatalog is null)
        {
            return [];
        }

        try
        {
            var registry = await mcpServerCatalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            // AC-869: cockpit-github-pull-requests is Internal (hidden from every picker); a git-repo working
            // directory names it explicitly here rather than through operator config.
            var autoMounted = await GitHubPullRequestsAutoMount.NamesAsync(worktreeManager, workingDirectory, cancellationToken).ConfigureAwait(false);
            var effectiveSelection = McpServerRegistryFilter.WithAutoMountedServers(enabledServerNames, registry, autoMounted);
            var eligible = McpServerRegistryFilter.ApplySessionSelection(registry, effectiveSelection)
                .Where(McpConfigFile.IsAgentEligible)
                .ToList();

            var servers = new List<PluginMcpServer>();
            foreach (var server in eligible)
            {
                // The endpoint first, because it changes what has to be asked for. Behind it the session never holds
                // a token at all, so it only has to be established that a sign-in exists; without it the token goes
                // into the config and has to outlast the whole sitting.
                var proxyUrl = await _ProxyUrlAsync(server, cancellationToken).ConfigureAwait(false);
                var access = await _AcquireCredentialAsync(server, proxyUrl is null, cancellationToken).ConfigureAwait(false);
                if (access.State == McpAuthState.AuthorizationRequired)
                {
                    // Left out rather than handed over bare: the CLI's initialize would meet the same refusal
                    // and report the server as failing instead of absent — worse, since that asks the operator
                    // to distinguish broken from unauthorized. The coordinator's own line names the cause.
                    continue;
                }

                if (PluginMcpServerMapper.ToPluginMcpServer(server, access.AccessToken, proxyUrl) is { } mapped)
                {
                    servers.Add(mapped);
                }
                else
                {
                    // AC-378: agent-eligible but no mountable transport (Http with no Url, Stdio with no
                    // Command — a misconfigured "SQL Explorer" surfaced this). Logged so this isn't silent.
                    logger?.LogWarning(
                        "MCP server {Name} is agent-eligible but not mountable (no url for Http / no command for Stdio); it was skipped.",
                        server.Name);
                }
            }

            // #44: say what the session got and against which selection, so "why are my MCP servers missing?"
            // is a log line, not a bisect. A non-empty selection resolving to nothing is usually a wiring slip — Warning; ordinary fan-out stays Information.
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

    // AC-353: the credential presented to `server`, resolved before the config is written and asked for
    // non-interactively — a token that cannot be renewed says so up front rather than surfacing as a later 401.
    // `theSessionWillHoldIt`: true when nothing stands in front of the server, so the token must outlast the session.
    private async Task<McpOAuthAccess> _AcquireCredentialAsync(McpServerConfig server, bool theSessionWillHoldIt, CancellationToken cancellationToken)
    {
        if (oauthCoordinator is null || server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        var access = theSessionWillHoldIt
            ? await oauthCoordinator.AcquireForSessionAsync(server, cancellationToken).ConfigureAwait(false)
            : await oauthCoordinator.AcquireAsync(server, interactive: false, cancellationToken).ConfigureAwait(false);
        if (access.State == McpAuthState.AuthorizationRequired)
        {
            // Information, not Warning: the coordinator already raises the operator's line once on the state
            // transition; repeating it at every session start is the nagging that made the last one easy to ignore.
            logger?.LogInformation(
                "This session starts without MCP server {Name}: {Guidance}",
                server.Name,
                McpOAuthSignInGuidance.For(server.Name, access.Reason));
        }

        return access;
    }

    // AC-524: the loopback address standing in for an OAuth server, or `null` (no proxy wired, not
    // applicable, or a listener that would not bind — all three mean the same thing here: write the token).
    private async Task<string?> _ProxyUrlAsync(McpServerConfig server, CancellationToken cancellationToken) =>
        oauthProxy is null ? null : await oauthProxy.MountAsync(server, cancellationToken).ConfigureAwait(false);

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment>? images = null, CancellationToken cancellationToken = default) =>
        inner.SendUserMessageAsync(
            text,
            images?.Select(image => new PluginImageAttachment(image.MediaType, image.Base64Data)).ToList(),
            cancellationToken);

    public Task InterruptAsync(CancellationToken cancellationToken = default) =>
        inner.InterruptAsync(cancellationToken);

    public Task CompactContextAsync(CancellationToken cancellationToken = default) =>
        inner.CompactContextAsync(cancellationToken);

    // A prompt the host's own tool loop raised is answered here; anything else is the plugin's own (AC-964).
    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _hostToolset?.Gate.Respond(toolUseId, allow) == true
            ? Task.CompletedTask
            : inner.RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        _hostToolset?.Gate.Respond(toolUseId, allow) == true
            ? Task.CompletedTask
            : inner.RespondToPermissionAsync(toolUseId, allow, answersJson, cancellationToken);

    // Both gates are told: the host's when it runs this session's tools, and the plugin's for its own, so a
    // provider that has both never ends up with one of them still prompting.
    public Task SetAutoApproveToolsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _hostToolset?.Gate.SetAutoApprove(enabled);
        return inner.SetAutoApproveToolsAsync(enabled, cancellationToken);
    }

    // AC-79: a delegated session has no operator to prompt, so its ceiling must reach the deciding gate — otherwise
    // the host-run loop raises a prompt nobody answers. AC-971: the same applies to the driver's own permission
    // stream, since a CLI-backed provider runs its built-in Write/Edit/Bash itself and asks only over its control protocol.
    public Task SetDelegatedToolGateAsync(string ceiling, IReadOnlyList<string> allowedTools, CancellationToken cancellationToken = default)
    {
        _hostToolset?.Gate.SetDelegatedGate(ceiling, allowedTools);

        // Allow-list before ceiling, same ordering rule as SessionToolApprovalGate.SetDelegatedGate: the ceiling
        // arms the gate, and a null one coerces to empty (armed, most restrictive) rather than to "unarmed".
        _delegatedAllowList = new HashSet<string>(allowedTools, StringComparer.Ordinal);
        _delegatedCeiling = ceiling ?? string.Empty;
        return Task.CompletedTask;
    }

    // The delegated ceiling and allow-list for this session's own (driver-raised) permission requests, or null for
    // an ordinary interactive session, where the operator answers them.
    private volatile string? _delegatedCeiling;
    private volatile IReadOnlySet<string>? _delegatedAllowList;

    // AC-971: answers one delegated permission request — never a prompt, never a hang. A CLI built-in is graded by
    // name; an `mcp__` tool is allowed (host can't grade it), bounded by the enabled-server set (AC-136/AC-378).
    // ponytail: a writing MCP tool bypasses a read-only ceiling (caught after via DelegatedWorkspaceChanges) — fix: carry the CLI's own MCP annotations to the host, as SessionToolApprovalGate already does.
    private async Task _DecideDelegatedPermissionAsync(string ceiling, PluginPermissionRequested permission, CancellationToken cancellationToken)
    {
        var onAllowList = _delegatedAllowList?.Contains(permission.ToolName) == true;
        var decision = DelegatedToolPermissionPolicy.ClassifyAgentBuiltIn(permission.ToolName) is { } builtInClass
            ? DelegatedToolPermissionPolicy.Decide(ceiling, builtInClass, permission.ToolName, onAllowList)
            : permission.ToolName.StartsWith("mcp__", StringComparison.Ordinal)
                ? PermissionDecision.Allow()
                : DelegatedToolPermissionPolicy.Decide(ceiling, ToolPermissionClass.Unknown, permission.ToolName, onAllowList);

        try
        {
            await inner.RespondToPermissionAsync(permission.ToolUseId, decision.IsAllowed, answersJson: null, decision.DenyMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A driver that cannot take the answer leaves the CLI waiting on a request nobody will resolve; log it
            // rather than tear the event stream down, since the turn's own timeout is what ends that.
            logger?.LogWarning(exception, "Could not answer the delegated permission request for tool {Tool}.", permission.ToolName);
        }
    }

    // D4: always-allow is session-scoped on the narrow plugin surface — forwarded so Codex's acceptForSession
    // can persist it, falling back to a one-time allow otherwise. Claude's cross-restart rule args have no
    // equivalent here. Host's gate goes first, same reason as RespondToPermissionAsync: it raised the prompt.
    public Task AllowPermissionAlwaysAsync(string toolUseId, string toolName, string proposedInputJson, PermissionRuleScope scope, CancellationToken cancellationToken = default) =>
        _hostToolset?.Gate.AllowAlways(toolUseId, toolName) == true
            ? Task.CompletedTask
            : inner.AllowPermissionAlwaysAsync(toolUseId, cancellationToken);

    // Fase 4 D4: no narrow-interface live-control channel, so these Claude-CLI-only ops wire the host's
    // permission-mode/model dropdowns to the plugin's generic live-option surface under well-known keys —
    // safe for every plugin, since one without the matching Supports* capability never has these called.
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        // AC-190 defense-in-depth: recompute so Capabilities never reports a stale "confined" after a live
        // switch — the Claude driver hides the bypass switch today, but this stops the guard resting on that fact staying true.
        _permissionModeConfines = _ModeConfines(mode);
        return inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.PermissionMode, mode, cancellationToken);
    }

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.Model, model ?? string.Empty, cancellationToken);

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Before the driver goes: the host's tool loop holds live MCP clients (and, for stdio servers, their
        // processes), and any prompt still waiting has to be refused rather than left hanging.
        if (_hostToolset is not null)
        {
            await _hostToolset.DisposeAsync().ConfigureAwait(false);
        }

        await _DisposeInnerAsync().ConfigureAwait(false);
    }

    private ValueTask _DisposeInnerAsync()
    {
        // The session's MCP identity goes with it rather than staying valid until the app restarts. Scoped to the
        // token this adapter minted, not the pane, since a restarting pane mints its replacement before disposal.
        // Interlocked takes-and-clears in one step: a close can land while StartAsync is still writing the field.
        if (Interlocked.Exchange(ref _minted, null) is { } minted)
        {
            keyring?.Revoke(minted.PaneId, minted.Token);
        }

        return inner.DisposeAsync();
    }

    // The per-session MCP token this adapter handed its session, so DisposeAsync can revoke exactly that one. Null
    // when the session runs on the shared app key (no pane id, or no keyring in a test graph). A reference rather than
    // a nullable tuple so Interlocked can carry it.
    private MintedToken? _minted;

    private sealed record MintedToken(string PaneId, string Token);

    // AC-408: the pane this session's conversation id is reported against, set once in StartAsync. Null when the
    // launch carried no pane id (a unit test wiring none), in which case there is nowhere to report to and
    // _ReportConversationIfChanged stays a no-op.
    private string? _paneId;

    // The last value handed to conversationSink, so a provider whose SessionId never changes (almost every one)
    // does not turn every single session event into a sink call — only a genuine change does (AC-408).
    private PluginConversationId? _lastReportedConversation;

    private async IAsyncEnumerable<SessionEvent> _AdaptEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var pluginEvent in _PluginEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            _ReportConversationIfChanged();

            // AC-971: a delegated session's permission requests are decided here, never published as a prompt with
            // nobody to answer it. The tool row and its (error) result still flow, so a denial stays visible.
            if (pluginEvent is PluginPermissionRequested permission && _delegatedCeiling is { } ceiling)
            {
                await _DecideDelegatedPermissionAsync(ceiling, permission, cancellationToken).ConfigureAwait(false);
                continue;
            }

            yield return _Adapt(pluginEvent);
        }
    }

    // One stream out of the driver's own events and — for a provider with a host-run tool loop (AC-964) — the tool
    // rows and permission prompts the host raises for it. The tool loop's stream ends with the driver's, since the
    // session is over either way; both are the same plugin event types, so they map through one translation below.
    private async IAsyncEnumerable<PluginSessionEvent> _PluginEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Read once, on the first move. SessionRuntime starts its pump only after awaiting StartAsync, so the
        // toolset is already mounted by then; a caller that enumerated first would silently get the driver's
        // events alone, which is why this reads it here rather than assuming it can never be null.
        if (_hostToolset is not { } toolset)
        {
            await foreach (var pluginEvent in inner.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return pluginEvent;
            }

            yield break;
        }

        var merged = Channel.CreateBounded<PluginSessionEvent>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var pumps = Task.WhenAll(
            _PumpAsync(inner.Events, merged.Writer, cancellationToken),
            _PumpAsync(toolset.Events.Events, merged.Writer, cancellationToken));
        _ = pumps.ContinueWith(_ => merged.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var pluginEvent in merged.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return pluginEvent;
        }
    }

    private static async Task _PumpAsync(IAsyncEnumerable<PluginSessionEvent> source, ChannelWriter<PluginSessionEvent> destination, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var pluginEvent in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await destination.WriteAsync(pluginEvent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The session is going away; the reader is ending on the same token.
        }
    }

    // Read off inner.Conversation (rather than the raw event's SessionId) so a driver that overrides Conversation
    // to Unsupported (AC-408 — an HTTP driver with its own in-memory history) is honoured here too, not just on
    // the SDK route's SessionId passthrough.
    private void _ReportConversationIfChanged()
    {
        if (conversationSink is null || _paneId is not { Length: > 0 } paneId)
        {
            return;
        }

        var conversation = inner.Conversation;
        if (conversation == _lastReportedConversation)
        {
            return;
        }

        _lastReportedConversation = conversation;
        conversationSink.Report(paneId, conversation.ToCore());
    }

    // AC-146: stamped once here rather than in every branch below — pluginEvent.ParentToolUseId names the
    // sub-agent (Task tool call) this event belongs to, when it belongs to one, and every SessionEvent subtype
    // carries the same base property the host's transcript nests sub-agent rows by.
    private static SessionEvent _Adapt(PluginSessionEvent pluginEvent) => _AdaptCore(pluginEvent) with
    {
        ParentToolUseId = pluginEvent.ParentToolUseId,
    };

    private static SessionEvent _AdaptCore(PluginSessionEvent pluginEvent) => pluginEvent switch
    {
        PluginSessionInitialized initialized => new SessionInitialized
        {
            SessionId = initialized.SessionId,
            Cwd = initialized.Cwd ?? string.Empty,
            Tools = initialized.Tools,
            Model = initialized.Model,
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
            Errors = turnCompleted.Errors,
        },
        PluginBackgroundTasksChanged backgroundTasks => new BackgroundTasksChanged
        {
            SessionId = backgroundTasks.SessionId,
            Tasks = [.. backgroundTasks.Tasks.Select(task => new BackgroundTask(
                task.TaskId,
                task.Kind switch
                {
                    PluginBackgroundTaskKind.SubAgent => BackgroundTaskKind.SubAgent,
                    PluginBackgroundTaskKind.Shell => BackgroundTaskKind.Shell,
                    _ => BackgroundTaskKind.Unknown,
                },
                task.Description))],
        },
        PluginBackgroundTaskNotification notification => new BackgroundTaskNotification
        {
            SessionId = notification.SessionId,
            TaskId = notification.TaskId,
            ToolUseId = notification.ToolUseId,
            Status = notification.Status switch
            {
                PluginBackgroundTaskStatus.Completed => BackgroundTaskStatus.Completed,
                PluginBackgroundTaskStatus.Failed => BackgroundTaskStatus.Failed,
                _ => BackgroundTaskStatus.Unknown,
            },
        },
        PluginSessionError error => new SessionError
        {
            SessionId = error.SessionId,
            Message = error.Message,
            Kind = error.Kind switch
            {
                PluginSessionErrorKind.AuthRequired => SessionErrorKind.AuthRequired,
                PluginSessionErrorKind.RateLimited => SessionErrorKind.RateLimited,
                PluginSessionErrorKind.ServiceUnavailable => SessionErrorKind.ServiceUnavailable,
                _ => SessionErrorKind.Unknown,
            },
            RetryAfter = error.RetryAfter,
        },
        _ => new UnknownEvent { SessionId = pluginEvent.SessionId, RawJson = string.Empty },
    };
}
