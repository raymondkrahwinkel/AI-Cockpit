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

// Wraps a plugin's narrow `IPluginSessionDriver` to satisfy the app's real `ISessionDriver`
// contract (#45) — the seam that lets `SessionDriverFactory` hand a plugin-backed session to the
// rest of the app unchanged. The Claude-CLI-only live-control members (permission mode / model / thinking-budget
// switch, always-allow rule persistence) have no equivalent in the narrow interface and are deliberate no-ops
// here, gated off in the UI by `Capabilities` reporting them unsupported.
internal sealed class PluginSessionDriverAdapter(IPluginSessionDriver inner, PluginSessionCapabilities pluginCapabilities, McpAuthKey authKey, IMcpServerCatalog? mcpServerCatalog = null, ILogger<PluginSessionDriverAdapter>? logger = null, SessionMcpKeyring? keyring = null, ISessionResourceResolver? sessionResources = null, IMcpOAuthCoordinator? oauthCoordinator = null, ISessionConversationSink? conversationSink = null, IMcpOAuthProxy? oauthProxy = null) : ISessionDriver
{
    // Live model switch / plan mode / thinking budget have no equivalent on the narrow IPluginSessionDriver
    // surface (no members could back them — see PluginSessionCapabilities) — always unsupported here rather
    // than a flag a plugin could set true with nothing behind it (#45 review finding 3). SupportsVision is
    // mapped straight through instead of forced false: every built-in example plugin already reports it
    // false (IPluginSessionDriver.SendUserMessageAsync has no images parameter yet, #64 fase 2), so this
    // stays honest without another host-side change once that surface can actually carry images.
    // The permission modes that keep a permission-based provider's file-access guard engaged (AC-190). A provider that
    // confines to its working directory only through its permission prompts (PluginSessionCapabilities.
    // ConfinesViaPermissionsOnly) genuinely confines in these; bypassPermissions (--dangerously-skip-permissions)
    // disables the guard, and any unrecognised mode is treated as not-confining — an allowlist, so a future mode is
    // refused until reviewed rather than silently trusted (fail closed).
    private static readonly IReadOnlySet<string> PermissionEngagedModes =
        new HashSet<string>(StringComparer.Ordinal) { "default", "acceptEdits", "plan" };

    // The mode assumed when a session carries no explicit permission-mode selection — the Claude driver's own default,
    // which keeps the permission system engaged (and so confines).
    private const string DefaultPermissionMode = "default";

    // Whether this session's effective permission mode keeps a permission-based provider's confinement engaged. Null
    // until StartAsync resolves it; a permission-based provider (ConfinesViaPermissionsOnly) reads null as "not yet
    // confirmed" and reports unconfined, so it fails closed before start and the isolation gate never proceeds on an
    // assumption.
    private bool? _permissionModeConfines;

    // Live model switch and permission-mode switch are now mapped straight through (Fase 4 D4): the narrow surface can
    // back them via SetLiveOptionAsync, which SetModelAsync/SetPermissionModeAsync below are wired to, so a plugin that
    // declares it (the Claude provider) drives the host's native model/permission dropdowns. Plan mode and thinking
    // budget still have no equivalent on the narrow surface and stay false.
    // ConfinesFileAccessToWorkingDirectory is recomputed on each read rather than fixed at construction (AC-190): for a
    // provider whose confinement rests on its permission system (ConfinesViaPermissionsOnly), a bypass permission mode
    // disables that guard, so the static registration capability would vouch a confinement the session does not deliver.
    // The host reads this instance capability after start, so the value reflects the session's resolved permission mode.
    public SessionCapabilities Capabilities => new(
        SupportsTools: pluginCapabilities.SupportsTools,
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
    };

    // The honest per-session confinement (AC-190). A provider that has not vouched confinement at all never confines.
    // A provider whose confinement is independent of its permission mode (a real OS sandbox — ConfinesViaPermissionsOnly
    // false) confines unconditionally. A permission-based provider confines only once the session's effective permission
    // mode is confirmed to keep the permission guard engaged; anything else (a bypass mode, or the mode not yet resolved
    // before start) reports unconfined, so the fail-closed isolation gate refuses an isolate-in-worktree run it cannot
    // vouch for.
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

    public async Task StartAsync(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectory = null, SessionResume? resume = null, IReadOnlyDictionary<string, string>? launchOptions = null, string? projectId = null, CancellationToken cancellationToken = default)
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

        var mcpServers = await _ResolveMcpServersAsync(selection, projectId, cancellationToken).ConfigureAwait(false);

        // AC-165: what the plugins give this session, resolved from the pane it is starting in so a contribution
        // can depend on the project that pane belongs to. AC-408: kept on the field too, so the event loop below
        // knows which pane to report a later conversation-id change against.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var pane) ? pane : null;
        _paneId = paneId;
        var contributed = sessionResources is null
            ? SessionResources.Empty
            : await sessionResources.ResolveAsync(paneId, cancellationToken).ConfigureAwait(false);

        // The host carries the operator's permission-mode selection as a typed parameter (a Claude concept older than
        // the plugin surface, which has no such parameter). Fold it into the options map under the well-known key so a
        // provider that declared a permission-mode option actually receives the choice — without it, a Claude plugin
        // session always fell back to the driver's own default (e.g. an operator's launch-time "bypassPermissions"
        // silently became "default"). The operator's explicit choice in the launch options wins; the typed value only
        // fills the key when the launch options carry none (see _MergePermissionMode) — folding it over an explicit
        // choice is what let a profile's stale default run a write tool ungated.
        var options = _StateAttendance(_MergePermissionMode(launchOptions, permissionMode));

        // Resolve, from the same effective options the driver starts with, whether this session's permission mode keeps a
        // permission-based provider's confinement engaged (AC-190). Read by Capabilities so the host's post-start
        // isolation gate sees the real per-session confinement rather than the static registration vouch — a Claude
        // session launched in bypassPermissions then reports unconfined and an isolate-in-worktree run is refused.
        _permissionModeConfines = _PermissionModeConfines(options);

        var environment = _SpawnEnvironment(profile, launchOptions, contributed);
        await inner.StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, environment, cancellationToken).ConfigureAwait(false);
    }

    // The environment the plugin driver receives: this run's MCP auth key (AC-40) so a cockpit-hosted server's
    // config can reference it instead of embedding a literal, plus the profile's own variables (AC-22) scrubbed
    // host-side — a variable on a host-controlled key (an `ANTHROPIC_*` credential, a nested-agent marker) is
    // dropped here, the same rule the TTY route applies, so no plugin has to be trusted to apply it. Dropping is
    // logged by name, never by value.
    //
    // What the plugins contribute for this session (AC-165) goes on last, so a project's answer beats the
    // profile's default — the precedence the rest of the app already follows where a project and a profile answer
    // the same question. It cannot reach the key above: that one is host-controlled, and a contribution's
    // host-controlled keys are gone before they arrive here.
    private IReadOnlyDictionary<string, string> _SpawnEnvironment(
        SessionProfile? profile,
        IReadOnlyDictionary<string, string>? launchOptions,
        SessionResources contributed)
    {
        // AC-89: hand a session that has a pane id (the App passes it as the cockpit.pane-id launch option) its own
        // per-session token as COCKPIT_MCP_KEY instead of the shared app key, so the consent broker can attribute a
        // request to the real session rather than trust the id the agent declares. No pane id (or no keyring in a test
        // graph) falls back to the shared key.
        var paneId = launchOptions is not null && launchOptions.TryGetValue(WellKnownPluginSessionOptions.PaneId, out var value) ? value : null;
        var mcpKey = keyring is not null && !string.IsNullOrEmpty(paneId) ? keyring.TokenFor(paneId) : authKey.Value;

        // Remembered so this session's token dies with it (AC-89). Only when we minted one: the shared app key is the
        // app's, not ours to drop.
        if (keyring is not null && !string.IsNullOrEmpty(paneId))
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

    // Whether the effective launch options' permission mode keeps a permission-based provider's confinement engaged
    // (AC-190). No permission-mode key means the driver falls back to its own default (which confines); an explicit
    // value is confining only when it is on the permission-engaged allowlist — bypassPermissions and any unrecognised
    // mode are not, so the session reports unconfined (fail closed).
    private static bool _PermissionModeConfines(IReadOnlyDictionary<string, string>? options) =>
        _ModeConfines(options is not null
            && options.TryGetValue(WellKnownPluginSessionOptions.PermissionMode, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null);

    // Whether a permission-mode string keeps a permission-based provider's confinement engaged (AC-190): the mode is on
    // the engaged allowlist, or absent (the driver's own confining default). bypassPermissions and any unrecognised mode
    // are not — fail closed. Shared by the start-time resolution and the live-switch recompute so both read the guard
    // the same way.
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

    // Says out loud, on every launch, whether an operator is watching this session (AC-378). Only the caller that
    // created the session knows — `DelegationService` and a self-driving embedded run write `"true"`; every
    // other launch is a pane someone opened, and gets an explicit `"false"` here rather than silence.
    //
    // Explicit, because a driver reading nothing has to assume the safe answer — unattended, so its tool narrowing
    // binds — and a driver that assumed the other way would hand a delegated agent the operator's own account
    // connectors the moment it ran on a host too old to state this (`PluginLoadPolicy` only enforces
    // `minHostVersion` from host major 1, so the manifest gate cannot be relied on to keep that pairing apart).
    // Stating it here makes the newer host's answer the one that travels, and leaves the older host's silence
    // meaning exactly what it meant before this split existed.
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

    // Turns the operator's per-session MCP selection into the concrete endpoints the plugin driver exposes:
    // the shared per-session narrowing (`McpServerRegistryFilter.ApplySessionSelection`, the same
    // one `ClaudeCliProcess` and the local-model tool-loop apply) intersected with the agent-eligible
    // servers (`McpConfigFile.IsAgentEligible`). The registry lives host-side (plugin isolation
    // keeps it out of the driver), so the adapter resolves names to definitions here. No store (a unit test
    // that does not wire one) means no fan-out. Best-effort — a transient `cockpit.json` read failure
    // launches the session without the shared servers rather than failing the whole start, matching how the
    // Claude fan-out treats the same read. `projectId` (AC-218) scopes the registry read to that
    // project's own view, so a project's servers and by-name overrides are seen here too.
    private async Task<IReadOnlyList<PluginMcpServer>> _ResolveMcpServersAsync(IReadOnlySet<string>? enabledServerNames, string? projectId, CancellationToken cancellationToken)
    {
        if (mcpServerCatalog is null)
        {
            return [];
        }

        try
        {
            var registry = await mcpServerCatalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            var eligible = McpServerRegistryFilter.ApplySessionSelection(registry, enabledServerNames)
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
                    // Left out rather than handed over bare, and this is the one place where that choice is made.
                    // Handing it over anyway would not save it: the CLI runs its initialize the moment the session
                    // starts, that call would meet the same refusal, and the server would be reported as failing
                    // instead of as absent — a worse answer, because it also asks the operator to distinguish a
                    // broken server from an unauthorized one. What they get instead is the coordinator's single line
                    // naming the cause and the action, and the Information line below naming this session. The case
                    // this ticket exists for — a sign-in that dies while the session runs — is not this one, and is
                    // handled at the endpoint above, where a call can still be answered.
                    continue;
                }

                if (_ToPluginMcpServer(server, access.AccessToken, proxyUrl) is { } mapped)
                {
                    servers.Add(mapped);
                }
                else
                {
                    // AC-378: the registry advertised this server as agent-eligible (enabled, in scope), but it has
                    // no transport target this driver can mount (an Http entry with no Url, a Stdio entry with no
                    // Command — a misconfigured "SQL Explorer" is the concrete case that surfaced this). Silently
                    // dropping it here is what let a profile/narrowing resolution quietly end up with fewer servers
                    // than advertised; logging it turns that into a line the operator can see instead of a session
                    // that mysteriously has fewer tools than the profile listing promised.
                    logger?.LogWarning(
                        "MCP server {Name} is agent-eligible but not mountable (no url for Http / no command for Stdio); it was skipped.",
                        server.Name);
                }
            }

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

    // The credential this session presents to `server`, resolved before the config is written
    // (AC-353). Asked for non-interactively: starting a session is not the moment to make a browser window appear,
    // so a token that cannot be renewed silently leaves the server unauthorized — and says so, because the whole
    // point is that this is known before the first tool call rather than surfacing as a 401 from the depths.
    //
    // `theSessionWillHoldIt`:
    // Whether the credential goes into the config and stays there. It does when nothing stands in front of the
    // server, and then it has to outlast the session — a token with minutes left is a session that loses this
    // server minutes in, which is the defect this ticket was opened for. Behind the loopback endpoint the answer is
    // only ever used to establish that a sign-in exists, because the endpoint asks again on every call.
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
            // Information, not a warning: the coordinator raises the operator's line once, on the transition into
            // this state, and repeating it at every session start is the nagging that made the last one easy to
            // ignore. This one records which session lost which server, and carries the same advice.
            logger?.LogInformation(
                "This session starts without MCP server {Name}: {Guidance}",
                server.Name,
                McpOAuthSignInGuidance.For(server.Name, access.Reason));
        }

        return access;
    }

    // The loopback address that stands in for an OAuth server (AC-524), or `null` to keep the old
    // behaviour. Null covers three things on purpose — no proxy wired (a unit test), a server this does not apply
    // to, and a listener that would not bind — because all three mean the same thing here: write the token.
    private async Task<string?> _ProxyUrlAsync(McpServerConfig server, CancellationToken cancellationToken) =>
        oauthProxy is null ? null : await oauthProxy.MountAsync(server, cancellationToken).ConfigureAwait(false);

    // HTTP → url with the credential this server needs (a static API key, or the token from the cockpit's own OAuth
    // sign-in — AC-353), plus a CockpitHosted flag for a cockpit loopback endpoint (whose auth rides the
    // COCKPIT_MCP_KEY env var, not a literal here — AC-40); stdio → command/args. A server missing its transport
    // target is dropped.
    private static PluginMcpServer? _ToPluginMcpServer(McpServerConfig server, string? oauthAccessToken, string? oauthProxyUrl) => server.Transport switch
    {
        // An OAuth server the cockpit put a loopback endpoint in front of (AC-524) is addressed there instead, and
        // carries no literal token at all: its auth is the same COCKPIT_MCP_KEY env reference every cockpit-hosted
        // endpoint uses, and the real credential is put on each request as it passes through. The session's config
        // file therefore holds no OAuth token to go stale — or to be read by another process on this machine.
        McpTransport.Http when oauthProxyUrl is { Length: > 0 } => new PluginMcpServer
        {
            Name = server.Name,
            Url = oauthProxyUrl,
            Headers = McpAgentHeaders.For(server, null),
            CockpitHosted = true,
        },
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserCredential(server, oauthAccessToken),
            Headers = McpAgentHeaders.For(server, CockpitMcpBearer.UserCredential(server, oauthAccessToken)),
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

    public Task CompactContextAsync(CancellationToken cancellationToken = default) =>
        inner.CompactContextAsync(cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        inner.RespondToPermissionAsync(toolUseId, allow, cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        inner.RespondToPermissionAsync(toolUseId, allow, answersJson, cancellationToken);

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
    public Task SetPermissionModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        // Keep the per-session confinement honest across a live permission-mode switch (AC-190 defense-in-depth):
        // recompute whether the new mode keeps the guard engaged, so Capabilities never reports a stale "confined" after
        // a switch. The Claude driver only offers confining live modes today (it hides the bypass switch), but this stops
        // the guard from resting on that staying true — a mode the host cannot vouch confines now reports unconfined.
        _permissionModeConfines = _ModeConfines(mode);
        return inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.PermissionMode, mode, cancellationToken);
    }

    public Task SetModelAsync(string? model, CancellationToken cancellationToken = default) =>
        inner.SetLiveOptionAsync(WellKnownPluginSessionOptions.Model, model ?? string.Empty, cancellationToken);

    public Task SetMaxThinkingTokensAsync(int maxThinkingTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        // The session is over, so its MCP identity goes with it rather than staying a valid bearer until the app
        // restarts. Scoped to the token this adapter minted: a restarting pane mints its replacement before the old
        // driver is disposed, and dropping by pane alone would revoke the live session's token instead of this one.
        // Taken and cleared in one step: a close can land while StartAsync is still writing the field on another
        // continuation (the runtime is registered before its start is awaited), and Interlocked gives both the barrier
        // that makes the write visible and the guarantee that a second dispose cannot revoke a second time.
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
        await foreach (var pluginEvent in inner.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            _ReportConversationIfChanged();
            yield return _Adapt(pluginEvent);
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
