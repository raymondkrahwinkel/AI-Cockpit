using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// `IPluginSessionDriver` for opencode.ai over the Agent Client Protocol (AC-783) — built to the shape of
// `Cockpit.Plugin.KimiProvider.KimiAcpSessionDriver` (the same host/pump/permission architecture: two
// background pumps drain `OpencodeAcpConnection.Notifications`/`ServerRequests`, `SendUserMessageAsync` is
// fire-and-forget because `session/prompt` only settles at turn end, permission requests route through
// Cockpit's own consent card), but re-measured rather than copied wherever the ticket asked for it —
// several real differences from Kimi came out of that measurement, documented below and at each site they
// change:
//
// 1. Usage/cost reporting (criterion 2, "no quota/cost" limitation): FALSE for opencode. A dedicated
//    `usage_update` session/update variant arrived unprompted after every real turn in this session's live
//    probing — `{"sessionUpdate":"usage_update","used":8507,"size":200000,"cost":{"amount":0,"currency":"USD"}}`
//    — so this driver reads it directly (see `_HandleNotification`) instead of Kimi's `/usage`-as-a-fake-turn
//    scrape, which also means none of Kimi's `_promptGate`-for-usage-polling machinery is needed here at all:
//    `_promptGate` below exists only to stop two real turns overlapping, not to serialise a poll against them.
// 2. Permission-by-default (criterion 3): opencode does NOT ask permission for edit/bash tools by default —
//    measured live, a plain `session/new` with no project config executed a file write with zero
//    `session/request_permission`. Asking is controlled entirely by opencode's own `permission` config
//    (`opencode.json`, project or global), which the ACP wire gives no session-scoped way to set — an inline
//    `permission` field on `session/new` params was silently ignored in this session's testing. The one
//    mechanism that did work, live-verified, is the `OPENCODE_CONFIG_CONTENT` environment variable (documented
//    on opencode.ai/docs/config as the highest-priority config layer short of an org-managed lock) — see
//    `StartAsync`, which sets it on every spawn so a permission request reaches this driver (and therefore
//    Cockpit's consent card) regardless of what the target project's own config says.
// 3. Model/mode configOptions (criterion 6/AC-272-equivalent): measured live as `[{id:"model",...},{id:"mode",...}]`
//    only — no "thinking" id the way Kimi has. Rather than hardcode a fixed valid-id list the way Kimi does
//    (defensible there because Kimi's own protocol note says exactly three configIds exist, full stop), this
//    driver validates a `SetLiveOptionAsync` key against whatever `_liveOptions` the current session snapshot
//    actually reports — correct for a CLI this session only observed one model's worth of configOptions from.
// 4. StopReason (criterion 2, "failed turn indistinguishable" limitation): the base Agent Client Protocol spec
//    (agentclientprotocol.com/protocol/prompt-turn) defines five stop reasons — end_turn, max_tokens,
//    max_turn_requests, refusal, cancelled — not the three-value fold Kimi's own SDK performs before handing
//    a reason to the client. Only end_turn and cancelled were exercised live against opencode 1.18.18 in this
//    session; max_tokens/max_turn_requests/refusal are mapped per the published spec text below, not
//    independently reproduced here. Whether opencode actually emits the full five or folds them the way Kimi's
//    SDK does was not settled by this session's testing — see `_MapStopReason`.
// 5. Environment passthrough: unlike Kimi (a single-vendor CLI, where an inherited ANTHROPIC_*/CLAUDE_CODE_*
//    credential is unambiguously foreign), opencode is explicitly multi-provider — its own docs and ACP
//    integration examples pass provider credentials like `OPENCODE_API_KEY` through the environment by
//    design, and a shell that already has e.g. `ANTHROPIC_API_KEY` set for other tools may well be relying on
//    opencode picking it up as one of its own supported providers. This driver does not scrub any inherited
//    credential family the way Kimi's StartAsync does — there is no "foreign" vendor for a model-agnostic CLI.
// 6. System prompt: no `session/new`/ACP-base-spec parameter for one was found in this session's research —
//    same unclaimed-capability conclusion as Kimi, but this is a protocol-level gap (the base ACP spec itself
//    defines no such parameter), not something specific to either agent's own implementation.
internal sealed class OpencodeAcpSessionDriver : IPluginSessionDriver
{
    private const string _ClientName = "Cockpit";
    private const string _ClientVersion = "1.0.0";

    // The most outstanding `session/request_permission`s tracked at once — exposed for tests. Same defensive
    // cap Kimi's driver uses: an OOM vector on untrusted stdout is the same shape regardless of which ACP
    // agent is on the other end of the pipe.
    internal const int MaxPendingApprovals = 500;

    // opencode's own "mode" configId has exactly two values (measured live: "build", "plan" — see the class
    // remarks). Cockpit's host-side permission-mode vocabulary has four; unlike Kimi (four Kimi-side modes for
    // four host-side ones), two of Cockpit's collapse onto "build" here. acceptEdits fails closed to "build"
    // (which itself only avoids prompting when this driver's own OPENCODE_CONFIG_CONTENT policy allows it —
    // see _OpencodePermissionPolicyJson) rather than a hypothetical looser tier opencode does not have, the
    // same fail-closed reasoning Kimi's own dictionary comment gives.
    private static readonly IReadOnlyDictionary<string, string> _PermissionModeToOpencodeMode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["default"] = "build",
        ["acceptEdits"] = "build",
        ["plan"] = "plan",
        ["bypassPermissions"] = "build",
    };

    private readonly OpencodeAcpConnection _connection;
    private readonly OpencodeConfig _config;
    private readonly string _executablePath;
    private readonly PluginSessionEventPublisher _events = new();
    private readonly CancellationTokenSource _lifetime = new();

    // toolCallId -> the pending session/request_permission's id + offered "options", bundled into one
    // immutable entry via TryAdd rather than two dictionaries updated independently — a child process that
    // reuses a toolCallId gets rejected outright instead of overwriting the entry the operator is looking at.
    private readonly ConcurrentDictionary<string, OpencodePendingApproval> _pendingApprovals = new();

    // Serialises every session/prompt send so two real turns can never overlap on the same session — simpler
    // than Kimi's own gate, which additionally had to serialise a synthetic /usage-poll "turn" against real
    // ones; this driver has no such poll (see the class remarks, point 1).
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    // True from the moment a cancel goes out until the next turn starts. Written on the caller's thread in
    // InterruptAsync/SendUserMessageAsync and read on the server-request pump's own task, hence volatile.
    private volatile bool _cancelling;

    // Read from Status (any thread the host polls from) and written from the notification pump's own task —
    // volatile so a reader never sees a stale reference.
    private volatile PluginSessionStatus? _status;

    // One instance per session, since it tracks per-toolCallId state (the lazy tool_call/tool_call_update
    // refinement sequence) across the whole session's notification stream.
    private readonly OpencodeSessionUpdateMapper _toolCallMapper = new();

    // Serialises "decide who owns this toolCallId's one PluginToolUseRequested" with "write it to the
    // channel". Two independent pump tasks (notifications, server requests) can both touch the same toolCallId
    // — see KimiAcpSessionDriver's own remarks for the exact race this closes. Only ever held around
    // synchronous channel writes, never across an await, so it cannot deadlock the pumps against each other.
    private readonly object _emitGate = new();

    private string? _model;
    private IReadOnlyDictionary<string, string>? _profileEnvironment;
    private JsonElement? _agentCapabilities;
    private JsonElement? _authMethods;
    private string? _sessionId;
    private Task? _notificationPump;
    private Task? _serverRequestPump;

    // Every fire-and-forget task launched this session (SendUserMessageAsync's own turns), so DisposeAsync can
    // await all of them, not just the latest — nothing in the contract stops a caller sending a second message
    // before the first turn settles.
    private readonly ConcurrentBag<Task> _launchedTasks = [];

    // Set before the lifetime token is cancelled in DisposeAsync, so the notification pump's shutdown path can
    // tell "we tore this down on purpose" apart from "the process just ended" — only the latter needs the
    // crash signal below.
    private volatile bool _disposing;

    // DisposeAsync must be idempotent — a second call must not re-cancel/re-dispose the already-disposed
    // CancellationTokenSource/SemaphoreSlim below.
    private bool _disposed;

    // The configOptions snapshot, rebuilt from session/new|resume, session/set_config_option and
    // config_option_update — the authoritative source for LiveOptions and for SetLiveOptionAsync's own
    // client-side validation (see the class remarks, point 3). Volatile — read from LiveOptions on whatever
    // thread the host polls from, written from StartAsync/SetLiveOptionAsync/the notification pump.
    private volatile IReadOnlyList<PluginSessionLaunchOption> _liveOptions = [];

    public OpencodeAcpSessionDriver(Func<ICliSubprocess> subprocessFactory, OpencodeConfig config, string executablePath)
    {
        _connection = new OpencodeAcpConnection(subprocessFactory());
        _config = config;
        _executablePath = executablePath;
        _model = config.DefaultModel;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true)
    {
        SupportsEnvVars = true,

        // WellKnownPluginSessionOptions.Model is the literal string "model" — the same id opencode's own
        // configOptions uses (measured live), so the host's live-model-switch wiring reaches
        // SetLiveOptionAsync with a key this driver already understands, no translation needed.
        SupportsLiveModelSwitch = true,
    };

    public string? SessionId => _sessionId;

    public int? ProcessId => _connection.ProcessId;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    // Filled directly from the live `usage_update` session/update stream (see _HandleNotification) — null
    // until the first one arrives, and left as-is by a turn that never produced one (a failed/cancelled turn
    // must never disturb a status the operator was already shown).
    public PluginSessionStatus? Status => _status;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    // The initialize response's `agentCapabilities` (loadSession, promptCapabilities, mcpCapabilities,
    // sessionCapabilities), or `null` before `StartAsync(string?, CancellationToken)` completes.
    internal JsonElement? AgentCapabilities => _agentCapabilities;

    // The initialize response's `authMethods` — measured live: opencode advertises exactly one,
    // `{"id":"opencode-login","name":"Login with opencode","description":"Run `opencode auth login` in the terminal"}`
    // — or `null` before `StartAsync(string?, CancellationToken)` completes. Not otherwise acted on by this
    // driver (kept alongside AgentCapabilities for a future config-view/UI, same as Kimi's own field).
    internal JsonElement? AuthMethods => _authMethods;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

    // The environment-carrying overload: the profile's variables arrive host-scrubbed; stash them so the spawn
    // below lays them under the config's own variables (the API key) and this driver's own forced permission
    // policy, which keep the last word.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        _profileEnvironment = environment;
        return StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, cancellationToken);
    }

    public async Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            _model = model;
        }

        var effectiveModel = _ResolveOption(options, WellKnownPluginSessionOptions.Model, _model);
        _model = effectiveModel;

        var processWorkingDirectory = _ResolveProcessWorkingDirectory(workingDirectory);
        var environmentVariables = _config.BuildEnvironmentVariables();
        foreach (var (key, value) in _profileEnvironment ?? new Dictionary<string, string>())
        {
            if (!environmentVariables.ContainsKey(key))
            {
                environmentVariables[key] = value;
            }
        }

        // AC-783 criterion 3: force a permission-ask (or, for an operator who explicitly chose full autonomy,
        // allow) policy via the one mechanism that is live-verified to reach opencode's own permission engine
        // regardless of the target project's own opencode.json — see the class remarks, point 2. This
        // deliberately overrides any permission rule a project's own config carries while a Cockpit profile is
        // driving the session: Cockpit's consent card is meant to be the permission surface for this session,
        // the same principle every other ACP/TTY provider in this tree already follows.
        var permissionMode = _ResolveOption(options, WellKnownPluginSessionOptions.PermissionMode, fallback: null);
        environmentVariables["OPENCODE_CONFIG_CONTENT"] = _OpencodePermissionPolicyJson(permissionMode);

        _connection.Start(_executablePath, processWorkingDirectory, environmentVariables);
        _notificationPump = Task.Run(() => _PumpNotificationsAsync(_lifetime.Token), CancellationToken.None);
        _serverRequestPump = Task.Run(() => _PumpServerRequestsAsync(_lifetime.Token), CancellationToken.None);

        var initializeResult = await _connection.SendRequestAsync("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
            clientInfo = new { name = _ClientName, version = _ClientVersion },
        }, cancellationToken).ConfigureAwait(false);

        _agentCapabilities = initializeResult.TryGetProperty("agentCapabilities", out var agentCapabilities) ? agentCapabilities.Clone() : null;
        _authMethods = initializeResult.TryGetProperty("authMethods", out var authMethods) ? authMethods.Clone() : null;

        // cwd must be absolute — normalising here means a caller-supplied relative path is still a real path
        // on the wire rather than a silent contract violation (matches Kimi's own StartAsync).
        var absoluteCwd = Path.GetFullPath(processWorkingDirectory);
        var mcpServersWire = OpencodeMcpConfig.Build(mcpServers, environmentVariables);

        JsonElement sessionResult;
        try
        {
            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                // session/resume, never session/load — the same D1 reasoning Kimi's driver documents: load
                // replays the whole history as session/update notifications before its response settles,
                // doubling the transcript Cockpit already keeps itself. resume's response carries no sessionId
                // of its own — the id is the one we already gave it.
                sessionResult = await _connection.SendRequestAsync("session/resume", new { cwd = absoluteCwd, sessionId = resumeSessionId, mcpServers = mcpServersWire }, cancellationToken).ConfigureAwait(false);
                _sessionId = resumeSessionId;
            }
            else
            {
                sessionResult = await _connection.SendRequestAsync("session/new", new { cwd = absoluteCwd, mcpServers = mcpServersWire }, cancellationToken).ConfigureAwait(false);
                _sessionId = _TryGetString(sessionResult, "sessionId", out var newSessionId)
                    ? newSessionId
                    : throw new OpencodeAcpException("session/new did not return a sessionId.");
            }
        }
        catch (OpencodeAcpException exception)
        {
            // Unlike Kimi's StartAsync, this does not match a specific JSON-RPC code for "not authenticated" —
            // that code was never exercised live (this session's own testing used opencode's built-in free
            // models, which need no auth at all), so matching one here would be exactly the kind of unmeasured
            // assumption AC-783 asks not to make. The agent's own error text is preserved and a general,
            // actionable pointer is appended instead — the readable-error path criterion 4 asks for, without
            // pretending to know a failure mode this session never saw.
            var actionableMessage = $"opencode could not start a session: {exception.Message} If this is an authentication problem, set an API key in this provider's configuration or run \"opencode auth login\" in a terminal, then try again.";
            _events.Publish(new PluginSessionError { SessionId = _sessionId, Message = actionableMessage, Kind = PluginSessionErrorKind.Unknown });
            throw new OpencodeAcpException(actionableMessage, exception.Code ?? 0);
        }

        if (sessionResult.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array)
        {
            _liveOptions = _BuildLiveOptions(configOptions);
        }

        _events.Publish(new PluginSessionInitialized { SessionId = _sessionId, Tools = [], Cwd = absoluteCwd });

        _ReportUnappliedSystemPrompt(options);

        // Fold the host's well-known permission-mode option into opencode's own "mode" configId, once, at
        // start — there is no such parameter on session/new/resume itself (same D8-class reasoning as Kimi).
        // Absent option = leave opencode's own default mode alone.
        if (permissionMode is not null && _PermissionModeToOpencodeMode.TryGetValue(permissionMode, out var opencodeMode))
        {
            await SetLiveOptionAsync("mode", opencodeMode, cancellationToken).ConfigureAwait(false);
        }

        // A stale/unconfigured OpencodeConfig.DefaultModel must never fail the whole session start. Validate it
        // against the configOptions snapshot just rebuilt above so an id the snapshot positively excludes is
        // not even attempted, and wrap the attempt itself in try/catch as a best-effort belt-and-braces.
        if (!string.IsNullOrWhiteSpace(effectiveModel) && _ShouldAttemptModelSwitch(effectiveModel))
        {
            try
            {
                await SetLiveOptionAsync("model", effectiveModel, cancellationToken).ConfigureAwait(false);
            }
            catch (OpencodeAcpException)
            {
                // Best-effort: the session still starts on whatever model opencode's own snapshot already defaulted to.
            }
        }
    }

    // The permission policy forced onto every spawn via OPENCODE_CONFIG_CONTENT (see StartAsync's remarks and
    // the class remarks, point 2). "bypassPermissions" is the one host-side mode that means "run fully
    // autonomously, no operator prompts" — honouring that intent means actually allowing everything, not
    // asking anyway; every other mode (including no mode selected at all) defaults to asking for everything,
    // which is what makes AC-783 criterion 3 true by default rather than only when a project happens to have
    // its own opencode.json permission config.
    private static string _OpencodePermissionPolicyJson(string? permissionMode) =>
        permissionMode == "bypassPermissions"
            ? """{"permission":{"*":"allow"}}"""
            : """{"permission":{"*":"ask"}}""";

    // Skips the call only when the snapshot positively excludes this model (a "model" dimension is present and
    // this value is not among its choices) — no "model" dimension at all in the snapshot is not evidence either
    // way, so the attempt still goes ahead (best-effort try/catch above is what protects that case).
    private bool _ShouldAttemptModelSwitch(string model) =>
        _liveOptions.FirstOrDefault(option => option.Key == "model") is not { } modelOption || modelOption.Choices.Contains(model);

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_sessionId is not { Length: > 0 } sessionId)
        {
            throw new InvalidOperationException($"{nameof(SendUserMessageAsync)} was called before the session started.");
        }

        // A new turn reopens the door the cancel closed: from here on a permission request is a real question
        // again, not the tail of a turn the operator already stopped.
        _cancelling = false;

        // Fire-and-forget: session/prompt only settles at turn end, so awaiting it here would block the caller
        // for the whole turn. The turn's content streams through the notification pump instead. Tracked so
        // DisposeAsync can await it before disposing _promptGate.
        _launchedTasks.Add(_SendPromptAsync(sessionId, text, cancellationToken));
        return Task.CompletedTask;
    }

    private async Task _SendPromptAsync(string sessionId, string text, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement result;
            await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var prompt = new object[] { new { type = "text", text } };
                result = await _connection.SendRequestAsync("session/prompt", new { sessionId, prompt }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _promptGate.Release();
            }

            _EmitTurnCompleted(result);
        }
        catch (OperationCanceledException)
        {
            // The session is being torn down — nothing to report.
        }
        catch (Exception exception)
        {
            _events.Publish(new PluginSessionError { SessionId = _sessionId, Message = exception.Message });
        }
    }

    private void _EmitTurnCompleted(JsonElement promptResult)
    {
        var (stopReason, isError) = _MapStopReason(promptResult);
        _events.Publish(new PluginTurnCompleted { SessionId = _sessionId, Subtype = stopReason, Result = null, IsError = isError, StopReason = stopReason });
    }

    // The base Agent Client Protocol spec (agentclientprotocol.com/protocol/prompt-turn) defines five stop
    // reasons. Only "end_turn" and "cancelled" were exercised live against opencode 1.18.18 in this session;
    // "max_tokens"/"max_turn_requests"/"refusal" are mapped per the published spec text, not independently
    // reproduced here — unlike Kimi, whose own SDK is documented (in KimiAcpSessionDriver's own comment) to
    // fold every non-refusal outcome onto "end_turn" before the client ever sees it. Whether opencode does the
    // same folding, or genuinely reports the other three, was not settled by this session's testing; this
    // mapping takes the spec at its word until a future session measures otherwise. IsError is only ever true
    // for "refusal" — a limit being reached (max_tokens/max_turn_requests) is not the same claim as a turn
    // having failed, so neither is flagged as an error.
    private static (string StopReason, bool IsError) _MapStopReason(JsonElement promptResult)
    {
        var stopReason = promptResult.ValueKind == JsonValueKind.Object
            && promptResult.TryGetProperty("stopReason", out var stopReasonProperty) && stopReasonProperty.ValueKind == JsonValueKind.String
                ? stopReasonProperty.GetString() ?? "end_turn"
                : "end_turn";

        return stopReason switch
        {
            "end_turn" => ("end_turn", false),
            "cancelled" => ("cancelled", false),
            "max_tokens" => ("max_tokens", false),
            "max_turn_requests" => ("max_turn_requests", false),
            "refusal" => ("refusal", true),
            _ => ("end_turn", false),
        };
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionId is not { Length: > 0 } sessionId)
        {
            return;
        }

        // session/cancel is a notification: the already-outstanding session/prompt settles later with
        // stopReason "cancelled", handled by _SendPromptAsync/_EmitTurnCompleted when it arrives.
        await _connection.SendNotificationAsync("session/cancel", new { sessionId }, cancellationToken).ConfigureAwait(false);

        // Per spec, every permission request still outstanding when a cancel goes out must be answered with
        // outcome "cancelled" — an unanswered one blocks the agent forever. Re-snapshot and keep draining
        // rather than iterating a single snapshot once — opencode may still send a request_permission for this
        // turn while this loop is running. The flag set above is what bounds this loop: a request arriving now
        // is answered where it is received and never lands here, so the dictionary can only shrink.
        _cancelling = true;
        while (!_pendingApprovals.IsEmpty)
        {
            foreach (var toolUseId in _pendingApprovals.Keys.ToList())
            {
                if (_pendingApprovals.TryRemove(toolUseId, out var pending))
                {
                    await _connection.RespondAsync(pending.RequestId, new { outcome = new { outcome = "cancelled" } }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _RespondPermissionAsync(toolUseId, allow ? "allow_once" : "reject_once", cancellationToken);

    // "Allow always" only ever means approve_always on this wire — opencode has no separate concept for
    // "always for this exact call" vs. "always for this kind of call" (same three-option shape Kimi offers,
    // measured live), so the two are indistinguishable at the plugin level.
    public Task AllowPermissionAlwaysAsync(string toolUseId, CancellationToken cancellationToken = default) =>
        _RespondPermissionAsync(toolUseId, "allow_always", cancellationToken);

    private async Task _RespondPermissionAsync(string toolUseId, string desiredKind, CancellationToken cancellationToken)
    {
        if (!_pendingApprovals.TryRemove(toolUseId, out var pending))
        {
            return;
        }

        var optionId = _ResolveOptionId(pending.Options, desiredKind);
        await _connection.RespondAsync(pending.RequestId, new { outcome = new { outcome = "selected", optionId } }, cancellationToken).ConfigureAwait(false);
    }

    // Reads the optionId from the offered "options" list by kind, rather than guessing a canonical id blindly
    // — mirrors Kimi's own reasoning (a different tunnelled namespace could use a different id for the same
    // kind), even though this session only ever observed the one canonical set live
    // (once/always/reject -> allow_once/allow_always/reject_once). Falls back to the canonical id only when
    // the kind is genuinely absent.
    private static string _ResolveOptionId(JsonElement options, string kind)
    {
        if (options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                if (option.ValueKind == JsonValueKind.Object
                    && option.TryGetProperty("kind", out var kindProperty) && kindProperty.ValueKind == JsonValueKind.String
                    && kindProperty.GetString() == kind
                    && option.TryGetProperty("optionId", out var optionIdProperty) && optionIdProperty.ValueKind == JsonValueKind.String)
                {
                    return optionIdProperty.GetString() ?? _CanonicalOptionId(kind);
                }
            }
        }

        return _CanonicalOptionId(kind);
    }

    private static string _CanonicalOptionId(string kind) => kind switch
    {
        "allow_once" => "once",
        "allow_always" => "always",
        _ => "reject",
    };

    public async Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // Validate against the current session's own configOptions snapshot rather than a hardcoded id list
        // (see the class remarks, point 3) — a bad key would earn a JSON-RPC error from opencode, but filtering
        // client-side against what this session actually reported means a stale/renamed key never reaches the
        // agent at all, without risking excluding a legitimate id this session simply never observed. Only
        // rejects when the snapshot is non-empty AND positively excludes the key — an empty snapshot (nothing
        // known yet) is not evidence either way, the same reasoning _ShouldAttemptModelSwitch already applies
        // to the model id specifically; a strict "must appear in the snapshot" check would otherwise reject
        // every key, including a legitimate one, whenever configOptions momentarily reports none.
        if (_sessionId is not { Length: > 0 } sessionId
            || (_liveOptions.Count > 0 && _liveOptions.All(option => option.Key != key)))
        {
            return;
        }

        var result = await _connection.SendRequestAsync("session/set_config_option", new { sessionId, configId = key, value }, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array)
        {
            _liveOptions = _BuildLiveOptions(configOptions);
        }
    }

    // The configOptions shape: [{type,id,name,category,currentValue,options:[{value,name,description}]}] —
    // measured live to match Kimi's protocol description byte-for-byte. A shrinking set simply produces fewer
    // launch options — nothing here assumes a fixed set of ids.
    private static IReadOnlyList<PluginSessionLaunchOption> _BuildLiveOptions(JsonElement configOptions)
    {
        var result = new List<PluginSessionLaunchOption>(configOptions.GetArrayLength());
        foreach (var configOption in configOptions.EnumerateArray())
        {
            if (_TryBuildLaunchOption(configOption) is { } launchOption)
            {
                result.Add(launchOption);
            }
        }

        return result;
    }

    private static PluginSessionLaunchOption? _TryBuildLaunchOption(JsonElement configOption)
    {
        if (!_TryGetString(configOption, "id", out var id)
            || !configOption.TryGetProperty("options", out var choicesElement) || choicesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var label = _TryGetString(configOption, "name", out var name) ? name : id;
        var currentValue = _TryGetString(configOption, "currentValue", out var current) ? current : null;

        var choices = new List<string>();
        var choiceLabels = new Dictionary<string, string>();
        foreach (var choice in choicesElement.EnumerateArray())
        {
            if (!_TryGetString(choice, "value", out var value))
            {
                continue;
            }

            choices.Add(value);
            if (_TryGetString(choice, "name", out var choiceLabel))
            {
                choiceLabels[value] = choiceLabel;
            }
        }

        return new PluginSessionLaunchOption(id, label, choices, currentValue) { ChoiceLabels = choiceLabels.Count > 0 ? choiceLabels : null };
    }

    private async Task _PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var notification in _connection.Notifications.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                _HandleNotification(notification);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        finally
        {
            if (!_disposing)
            {
                // The process ended on its own — a crash, not our own dispose — rather than through
                // DisposeAsync. Emitting these two before completing the channel is the whole point: a bare
                // channel-complete would leave the UI with no reason the session stopped.
                _events.Publish(new PluginSessionError { SessionId = _sessionId, Message = "The opencode acp process ended unexpectedly." });
                _events.Publish(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
            }

            _events.TryComplete();
        }
    }

    private void _HandleNotification(OpencodeNotification notification)
    {
        if (notification.Method != "session/update")
        {
            return;
        }

        if (_TryGetUpdateDiscriminator(notification.Params, out var update, out var discriminator) && discriminator == "usage_update")
        {
            // Read directly, not through OpencodeSessionUpdateMapper — this is the class remarks' point 1: a
            // real, structured usage figure per turn, unlike Kimi (which has none on the wire at all and has
            // to scrape a /usage command's free-text reply instead). `cost.amount`/`cost.currency` were also
            // observed live (e.g. {"amount":0,"currency":"USD"}) but PluginSessionStatus has no field for an
            // absolute currency amount — only a percent-based PluginRateLimitWindow — so cost is read here and
            // then dropped: an honest, measured gap in the shared plugin contract, not a missing feature of
            // this plugin. A future host-side change to PluginSessionStatus could surface it; this driver does
            // not invent a percent to smuggle a dollar amount through RateLimits.
            if (_TryGetDouble(update, "used", out var used) && _TryGetDouble(update, "size", out var size) && size > 0)
            {
                _status = new PluginSessionStatus(used / size * 100, RateLimits: []);
            }

            return;
        }

        // Claim and write under the same gate as the permission path (see _emitGate): the mapper decides which
        // side owns a toolCallId's one PluginToolUseRequested, and whoever wins must have it in the channel
        // before the other side can write anything about that id.
        OpencodeSessionUpdateMapResult result;
        lock (_emitGate)
        {
            result = _toolCallMapper.Map(notification.Params);
            foreach (var evt in result.Events)
            {
                _events.Publish(evt);
            }
        }

        if (result.ConfigOptions is { } configOptions)
        {
            _liveOptions = _BuildLiveOptions(configOptions);
        }
    }

    private async Task _PumpServerRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _connection.ServerRequests.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await _HandleServerRequestAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
    }

    private async Task _HandleServerRequestAsync(OpencodeServerRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "session/request_permission":
                await _SurfacePermissionAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // An unmodelled reverse-request (fs/read_text_file, fs/write_text_file — never sent since we
                // advertise fs.* as false, or any future method) gets a JSON-RPC error, not a made-up success
                // the agent would act on as if it were real.
                await _connection.RespondErrorAsync(request.Id, -32601, $"Cockpit does not support '{request.Method}' yet.", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task _SurfacePermissionAsync(OpencodeServerRequest request, CancellationToken cancellationToken)
    {
        if (!_TryGetNestedString(request.Params, "toolCall", "toolCallId", out var toolCallId))
        {
            // A malformed request must not just be dropped — it is blocking, and a silent return leaves the
            // agent waiting forever with no card and no error to explain why.
            await _connection.RespondErrorAsync(request.Id, -32602, "session/request_permission is missing toolCall.toolCallId.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // A permission request that arrives once this turn is being cancelled is answered "cancelled" right
        // here and never tracked — the operator asked for the turn to stop, so a fresh card would be answering
        // a question nobody wants, and InterruptAsync's own drain loop below only terminates because nothing
        // new can enter the dictionary while it runs.
        if (_cancelling)
        {
            await _connection.RespondAsync(request.Id, new { outcome = new { outcome = "cancelled" } }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Caps concurrent outstanding permission requests — an unbounded _pendingApprovals dictionary is an
        // OOM vector on untrusted stdout.
        if (_pendingApprovals.Count >= MaxPendingApprovals)
        {
            await _connection.RespondErrorAsync(request.Id, -32000, $"Too many outstanding permission requests (max {MaxPendingApprovals}).", cancellationToken).ConfigureAwait(false);
            return;
        }

        var options = request.Params.TryGetProperty("options", out var optionsElement) ? optionsElement.Clone() : default;
        if (!_pendingApprovals.TryAdd(toolCallId, new OpencodePendingApproval(request.Id, options)))
        {
            // The child process reused a toolCallId that already has an outstanding approval. Overwriting the
            // entry would let this second request answer whichever card the operator is looking at for the
            // first (confused deputy) — reject it instead of resolving the wrong one.
            await _connection.RespondErrorAsync(request.Id, -32602, $"Duplicate session/request_permission for toolCallId '{toolCallId}'.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Measured live: opencode's toolCall.title on a permission request is the file path being touched
        // ("C:\...\hello.txt"), not a tool-family name the way Kimi's title reads ("write") — this is exactly
        // whatever opencode chose to put there, passed straight through as ToolName without reinterpretation.
        var toolName = _TryGetNestedString(request.Params, "toolCall", "title", out var title) ? title : "tool";

        // Without a prior PluginToolUseRequested for this same id, the permission card the host renders has no
        // matching tool call to attach its buttons to — emit one now if nothing else already did. Both the
        // check and the two writes happen under _emitGate — see the field's own remarks for why.
        lock (_emitGate)
        {
            if (_toolCallMapper.EnsureToolUseRequested(toolCallId, _sessionId, toolName) is { } toolUseRequested)
            {
                _events.Publish(toolUseRequested);
            }

            var toolCallJson = request.Params.TryGetProperty("toolCall", out var toolCall) ? toolCall.GetRawText() : "{}";
            _events.Publish(new PluginPermissionRequested { SessionId = _sessionId, ToolUseId = toolCallId, ToolName = toolName, InputJson = toolCallJson });
        }
    }

    // Says out loud that this session's hidden briefing — a profile's identity, a project's instructions, an
    // embedded Autopilot run's CEO prompt — is not reaching opencode. There is no route for it over ACP: the
    // base protocol defines no systemPrompt/instructions parameter on session/new, and this session's own
    // research found none of opencode's own extensions filling that gap either — the one text opencode does
    // read as project instructions is AGENTS.md (opencode.ai/docs/acp lists "Project-specific rules from
    // AGENTS.md" as supported), introduced the same way Kimi's own $KIMI_CODE_HOME/AGENTS.md is: a file the
    // operator maintains, not a channel this driver can write a per-session prompt into. So the capability
    // stays unclaimed and the gap is made visible instead, mirroring Kimi's own AC-273 precedent.
    private void _ReportUnappliedSystemPrompt(IReadOnlyDictionary<string, string>? options)
    {
        if (_ResolveOption(options, WellKnownPluginSessionOptions.AppendSystemPrompt, fallback: null) is null)
        {
            return;
        }

        _events.Publish(new PluginSessionError
        {
            SessionId = _sessionId,
            Message = "This session carries a system prompt (a profile identity, a project's instructions, or an "
                + "embedded run's briefing), but opencode has no way to receive one over ACP — it is not applied. "
                + "Put anything the agent must know in your first message instead.",
        });
    }

    private string _ResolveProcessWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return workingDirectory;
        }

        return string.IsNullOrWhiteSpace(_config.WorkingDirectory) ? Environment.CurrentDirectory : _config.WorkingDirectory;
    }

    // The operator's per-session launch-option choice wins over the profile's own configured default — the
    // same precedence Kimi's own driver applies.
    private static string? _ResolveOption(IReadOnlyDictionary<string, string>? options, string key, string? fallback) =>
        options is not null && options.TryGetValue(key, out var chosen) && !string.IsNullOrWhiteSpace(chosen) ? chosen : fallback;

    private static bool _TryGetString(JsonElement parent, string property, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool _TryGetDouble(JsonElement parent, string property, out double value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        value = 0;
        return false;
    }

    private static bool _TryGetUpdateDiscriminator(JsonElement notificationParams, out JsonElement update, out string discriminator)
    {
        if (notificationParams.ValueKind == JsonValueKind.Object
            && notificationParams.TryGetProperty("update", out update) && update.ValueKind == JsonValueKind.Object
            && _TryGetString(update, "sessionUpdate", out discriminator))
        {
            return true;
        }

        update = default;
        discriminator = string.Empty;
        return false;
    }

    private static bool _TryGetNestedString(JsonElement parent, string outerProperty, string innerProperty, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(outerProperty, out var outer))
        {
            return _TryGetString(outer, innerProperty, out value);
        }

        value = string.Empty;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposing = true;
        _events.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);

        foreach (var pump in new[] { _notificationPump, _serverRequestPump })
        {
            if (pump is not null)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected — the pumps observe the lifetime cancellation.
                }
            }
        }

        // Wait for every fire-and-forget turn task to actually finish releasing _promptGate before disposing
        // it below — each already reports its own failures through _events, so any exception surfacing here on
        // the await is not acted on further, only kept from becoming an unobserved task exception.
        foreach (var pendingTask in _launchedTasks.ToArray())
        {
            if (pendingTask is not null)
            {
                try
                {
                    await pendingTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already reported (or intentionally swallowed) by the task itself.
                }
            }
        }

        _lifetime.Dispose();
        _promptGate.Dispose();
    }
}
