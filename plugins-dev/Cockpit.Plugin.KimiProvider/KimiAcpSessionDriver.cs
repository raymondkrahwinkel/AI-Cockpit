using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// <see cref="IPluginSessionDriver"/> for Kimi Code over the Agent Client Protocol (AC-269/270/271/272).
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: <see cref="StartAsync(string?, string?, string?, IReadOnlyDictionary{string, string}?, IReadOnlyList{PluginMcpServer}?, CancellationToken)"/>
/// spawns <c>kimi acp</c>, does the <c>initialize</c> handshake (AC-268), then <c>session/new</c> or
/// <c>session/resume</c> — never <c>session/load</c> (D1): that variant replays the whole history as
/// <c>session/update</c> notifications before its response settles, which would double the transcript Cockpit
/// already renders itself. <see cref="SendUserMessageAsync"/> is fire-and-forget: <c>session/prompt</c> only
/// settles at turn end (protocol §3), so awaiting it here would block the caller for the whole turn; the
/// streaming content arrives through the notification pump instead, translated by
/// <see cref="KimiSessionUpdateMapper"/>, and the stop reason lands on the pump's background task.
/// </para>
/// <para>
/// Two background pumps, mirroring <c>CodexAppServerSessionDriver</c>: one drains <see cref="KimiAcpConnection.Notifications"/>
/// (the transcript), the other <see cref="KimiAcpConnection.ServerRequests"/> (blocking reverse-requests — permission
/// prompts, and anything unmodelled, which gets a JSON-RPC <c>-32601</c> rather than a made-up success, D11).
/// </para>
/// <para>
/// AC-274: <see cref="Status"/> is filled by polling the ACP-builtin <c>/usage</c> command as an ordinary
/// <c>session/prompt</c> — the only way to get token/context data out of kimi at all (protocol §11; no
/// <c>session/update</c> variant carries usage). Its one <c>agent_message_chunk</c> reply is parsed straight
/// into <see cref="_status"/> by <see cref="_HandleNotification"/> while <see cref="_capturingUsageResponse"/>
/// is set, never reaching the transcript or emitting a second <see cref="PluginTurnCompleted"/>. <see cref="_promptGate"/>
/// serialises every <c>session/prompt</c> send — real turn or usage poll — because the wire has no per-notification
/// turn id (protocol §11) to tell a poll's chunk apart from a running turn's; the poll only ever starts right
/// after a real turn settles (<see cref="_SendPromptAsync"/>), never against a timer and never mid-turn.
/// </para>
/// </remarks>
internal sealed class KimiAcpSessionDriver : IPluginSessionDriver
{
    private const string _ClientName = "Cockpit";
    private const string _ClientVersion = "1.0.0";

    /// <summary>The most outstanding <c>session/request_permission</c>s tracked at once (P1-9) — exposed for tests.</summary>
    internal const int MaxPendingApprovals = 500;

    // P1-10b: the JSON-RPC code kimi returns for "no usable auth token" (protocol §1) — a session/new|resume
    // that fails with exactly this code gets an actionable message instead of the raw JSON-RPC error text.
    private const int _AuthRequiredErrorCode = -32000;

    // Exactly three configIds exist on the wire (protocol §8); anything else would earn a -32602 from kimi, but
    // filtering here means a bad key never even reaches the agent (AC-272 sub [e]).
    private static readonly string[] _ValidConfigIds = ["model", "mode", "thinking"];

    // D8, security (P0-4): the host's well-known Claude-style permission-mode has no exact equivalent in Kimi's
    // four-value mode enum, and Kimi has no edits-only tier of its own. acceptEdits (the host's "a non-destructive
    // write is fine, nothing else runs unattended" ceiling) fails closed to "default" (manual approval prompts)
    // rather than escalating to "yolo", which disables session/request_permission entirely — shell commands and
    // deletes would then run with no operator-visible prompt at all, a silent privilege escalation past what the
    // host ceiling actually grants. A prompt the operator did not expect is safer than a tool that ran unseen.
    // bypassPermissions ("nothing is ever asked") is the only mode that legitimately maps to "auto" (fully
    // autonomous). default/plan map straight across since Kimi has the same names.
    private static readonly IReadOnlyDictionary<string, string> _PermissionModeToKimiMode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["default"] = "default",
        ["plan"] = "plan",
        ["acceptEdits"] = "default",
        ["bypassPermissions"] = "auto",
    };

    private readonly KimiAcpConnection _connection;
    private readonly KimiConfig _config;
    private readonly string _executablePath;
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();
    private readonly CancellationTokenSource _lifetime = new();

    // toolCallId -> the pending session/request_permission's id + offered "options" (P0-5), bundled into one
    // immutable entry via TryAdd rather than two dictionaries updated independently — a child process that
    // reuses a toolCallId gets rejected outright instead of overwriting the entry the operator is looking at.
    private readonly ConcurrentDictionary<string, KimiPendingApproval> _pendingApprovals = new();

    // AC-274: serialises every session/prompt send (real turns and the /usage poll alike) — see the class remarks.
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private volatile bool _capturingUsageResponse;

    // How long a /usage capture stays armed after the poll goes out. Generous by design: it only has to outlast
    // the notification pump's lag behind the poll's own reply continuation (milliseconds), and it exists purely
    // so an arm that never met a parsable reply cannot stay live for the rest of the session. Settable for tests,
    // which cannot wait ten seconds to prove a disarm.
    internal long UsageCaptureWindowMilliseconds { get; init; } = 10_000;

    // Written on the poll's own task alongside _capturingUsageResponse, read on the notification pump's.
    private long _usageCaptureDeadline;

    // True from the moment a cancel goes out until the next turn starts. Written on the caller's thread in
    // InterruptAsync/SendUserMessageAsync and read on the server-request pump's own task, hence volatile.
    private volatile bool _cancelling;

    // P1-11: read from Status (any thread the host polls from) and written from the notification pump's own
    // task — volatile, matching _capturingUsageResponse/_disposing above and the Codex driver template, so a
    // reader never sees a stale reference.
    private volatile PluginSessionStatus? _status;

    // P1-3: one instance per session, since it tracks per-toolCallId state (the lazy tool_call/tool_call_update
    // refinement sequence) across the whole session's notification stream.
    private readonly KimiSessionUpdateMapper _toolCallMapper = new();

    // Serialises "decide who owns this toolCallId's one PluginToolUseRequested" with "write it to the channel".
    // The two pumps below are independent tasks, and the mapper's claim is atomic on its own — but a claim that
    // has not reached the channel yet is invisible to the other pump, which would then order a permission card
    // ahead of the tool card it belongs to. Only ever held around synchronous channel writes, never across an
    // await, so it cannot deadlock the pumps against each other.
    private readonly object _emitGate = new();

    private string? _model;
    private IReadOnlyDictionary<string, string>? _profileEnvironment;
    private JsonElement? _agentCapabilities;
    private JsonElement? _authMethods;
    private string? _sessionId;
    private Task? _notificationPump;
    private Task? _serverRequestPump;

    // P1-6: the fire-and-forget tasks SendUserMessageAsync/_SendPromptAsync launch, tracked so DisposeAsync can
    // await them before disposing _promptGate — otherwise a Release() in _PollContextUsageAsync's finally
    // (which has no catch of its own) can race a disposed gate and fault as an unobserved task exception.
    private Task? _pendingUsagePollTask;

    // Every fire-and-forget task launched this session, so DisposeAsync can await all of them and not just the
    // latest of each kind — see the loop there.
    private readonly ConcurrentBag<Task> _launchedTasks = [];

    // Set before the lifetime token is cancelled in DisposeAsync, so the notification pump's shutdown path can
    // tell "we tore this down on purpose" apart from "the process just ended" (D12) — only the latter needs the
    // crash signal below.
    private volatile bool _disposing;

    // P1-6: DisposeAsync must be idempotent — a second call must not re-cancel/re-dispose the already-disposed
    // CancellationTokenSource/SemaphoreSlim below.
    private bool _disposed;

    // The configOptions snapshot (protocol §8), rebuilt from session/new|resume, session/set_config_option and
    // config_option_update — the authoritative source for LiveOptions; never a hardcoded model id or mode list.
    // P1-11: volatile — read from LiveOptions on whatever thread the host polls from, written from
    // StartAsync/SetLiveOptionAsync/the notification pump, each on its own task.
    private volatile IReadOnlyList<PluginSessionLaunchOption> _liveOptions = [];

    public KimiAcpSessionDriver(Func<ICliSubprocess> subprocessFactory, KimiConfig config, string executablePath)
    {
        _connection = new KimiAcpConnection(subprocessFactory());
        _config = config;
        _executablePath = executablePath;
        _model = config.DefaultModel;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true)
    {
        SupportsEnvVars = true,

        // WellKnownPluginSessionOptions.Model is the literal string "model" — the same id Kimi's own
        // configOptions uses, so the host's live-model-switch wiring reaches SetLiveOptionAsync with a key this
        // driver already understands, no translation needed.
        SupportsLiveModelSwitch = true,
    };

    public string? SessionId => _sessionId;

    public int? ProcessId => _connection.ProcessId;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    // AC-274: filled by the trailing /usage poll after a turn settles; null until the first successful poll,
    // and left as-is (not reset) by a failed one — a failed /usage round must never disturb the session.
    public PluginSessionStatus? Status => _status;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    /// <summary>
    /// The initialize response's <c>agentCapabilities</c> (loadSession, promptCapabilities, mcpCapabilities,
    /// sessionCapabilities), or <see langword="null"/> before <see cref="StartAsync(string?, CancellationToken)"/>
    /// completes.
    /// </summary>
    internal JsonElement? AgentCapabilities => _agentCapabilities;

    /// <summary>
    /// The initialize response's <c>authMethods</c> (protocol §1) — kimi advertises exactly one, the
    /// <c>type:"terminal"</c> <c>kimi acp --login</c> device-code flow — or <see langword="null"/> before
    /// <see cref="StartAsync(string?, CancellationToken)"/> completes (P1-10a). Not otherwise acted on by this
    /// driver: <see cref="_AuthRequiredErrorCode"/>'s handling below covers the one place it actually matters,
    /// but kept alongside <see cref="AgentCapabilities"/> so a future config-view/UI has it without another
    /// round trip through the initialize response.
    /// </summary>
    internal JsonElement? AuthMethods => _authMethods;

    /// <summary>The most recently launched trailing /usage poll task (P1-6), or <see langword="null"/> before the first one — exposed for tests only.</summary>
    internal Task? PendingUsagePollTaskForTests => _pendingUsagePollTask;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

    // The environment-carrying overload: the profile's variables arrive host-scrubbed; stash them so the spawn
    // below lays them under the config's own variables (the API key), which keep the last word.
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

        // P1-7: drop any inherited or profile-supplied credential of the agent stack the cockpit itself runs on
        // before spawning kimi acp. ANTHROPIC_* is the API key/token that would put a session on someone's
        // API billing; CLAUDECODE/CLAUDE_CODE_*/CLAUDE_AGENT_* are what a Claude Code session exports to mark
        // itself, and CLAUDE_CODE_OAUTH_TOKEN in particular is a live credential — a cockpit started from inside
        // such a session inherits them, and Moonshot's CLI has no business receiving any of it. Same families the
        // host strips for its own spawns (TtyEnvironment.IsHostControlled), minus two it strips for reasons that
        // do not apply here: terminal-identity markers (this child renders nothing — it speaks JSON-RPC over a
        // pipe) and COCKPIT_MCP_KEY, which this driver deliberately passes on because the MCP servers it hands
        // kimi authenticate with exactly that key. Null tells the subprocess seam to remove the variable from the
        // child's environment rather than merely not setting it, so an inherited one is actually stripped.
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Concat(environmentVariables.Keys)
                     .Where(_IsForeignAgentCredential)
                     .ToList())
        {
            environmentVariables[key] = null;
        }

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

        // cwd must be absolute (protocol §2) — normalising here means a caller-supplied relative path is still
        // a real path on the wire rather than a silent contract violation kimi would reject or misresolve.
        var absoluteCwd = Path.GetFullPath(processWorkingDirectory);
        var mcpServersWire = KimiMcpConfig.Build(mcpServers, environmentVariables);

        JsonElement sessionResult;
        try
        {
            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                // D1: session/resume, never session/load — load replays the whole history as session/update
                // notifications before its response settles, doubling the transcript Cockpit already keeps itself.
                // resume's response carries no sessionId of its own (protocol §2) — the id is the one we already gave it.
                sessionResult = await _connection.SendRequestAsync("session/resume", new { cwd = absoluteCwd, sessionId = resumeSessionId, mcpServers = mcpServersWire }, cancellationToken).ConfigureAwait(false);
                _sessionId = resumeSessionId;
            }
            else
            {
                sessionResult = await _connection.SendRequestAsync("session/new", new { cwd = absoluteCwd, mcpServers = mcpServersWire }, cancellationToken).ConfigureAwait(false);
                _sessionId = _TryGetString(sessionResult, "sessionId", out var newSessionId)
                    ? newSessionId
                    : throw new KimiAcpException("session/new did not return a sessionId.");
            }
        }
        catch (KimiAcpException exception) when (exception.Code == _AuthRequiredErrorCode)
        {
            // P1-10b, protocol §1: -32000 (authRequired) means kimi has no usable token on disk — session/new
            // and session/resume both fail this way with no route back to login of their own. Two ways past
            // it: an API key in this provider's config (skips the auth gate entirely — harnessIsAuthed's
            // non-OAuth-credential short-circuit), or kimi acp --login's own device-code flow. Surface both
            // instead of the raw JSON-RPC error text, and keep both channels StartAsync's failure can be seen
            // through in sync (P1-8's precedent: a thrown message a caller shows verbatim, e.g. SessionViewModel's
            // Status = $"Failed to start: {ex.Message}") — the event and the rethrown exception carry the same text.
            var actionableMessage = "Kimi is not authenticated. Set an API key in this provider's configuration, or run \"kimi acp --login\" to sign in, then try again.";
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = actionableMessage });
            throw new KimiAcpException(actionableMessage, exception.Code.Value);
        }

        if (sessionResult.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array)
        {
            _liveOptions = _BuildLiveOptions(configOptions);
        }

        _events.Writer.TryWrite(new PluginSessionInitialized { SessionId = _sessionId, Tools = [], Cwd = absoluteCwd });

        _ReportUnappliedSystemPrompt(options);

        // D8: fold the host's well-known permission-mode option into Kimi's own "mode" configId, once, at start —
        // there is no such parameter on session/new/resume itself. Absent option = leave kimi's own default mode alone.
        var permissionMode = _ResolveOption(options, WellKnownPluginSessionOptions.PermissionMode, fallback: null);
        if (permissionMode is not null && _PermissionModeToKimiMode.TryGetValue(permissionMode, out var kimiMode))
        {
            await SetLiveOptionAsync("mode", kimiMode, cancellationToken).ConfigureAwait(false);
        }

        // P1-5: a stale/unconfigured KimiConfig.DefaultModel must never fail the whole session start. Validate
        // it against the configOptions snapshot just rebuilt above (AC-272 "snapshot is authoritative") so an
        // id the snapshot positively excludes is not even attempted, and wrap the attempt itself in try/catch as
        // a best-effort belt-and-braces — an unconditional call here used to earn a -32602 that StartAsync threw
        // straight through, wrecking the whole session over a model rename/removal upstream.
        if (!string.IsNullOrWhiteSpace(effectiveModel) && _ShouldAttemptModelSwitch(effectiveModel))
        {
            try
            {
                await SetLiveOptionAsync("model", effectiveModel, cancellationToken).ConfigureAwait(false);
            }
            catch (KimiAcpException)
            {
                // Best-effort: the session still starts on whatever model kimi's own snapshot already defaulted to.
            }
        }
    }

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

        // Fire-and-forget: session/prompt only settles at turn end (protocol §3), so awaiting it here would
        // block the caller for the whole turn. The turn's content streams through the notification pump instead.
        // Tracked (P1-6) so DisposeAsync can await it before disposing _promptGate.
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

            // AC-274: right after a real turn settles is the one sober moment to poll /usage — no independent
            // timer, and never while this same gate has a turn's own prompt outstanding. Fire-and-forget: the
            // poll must not delay the turn-completed signal the caller is waiting on. Tracked (P1-6) so
            // DisposeAsync can await it before disposing _promptGate.
            _pendingUsagePollTask = _PollContextUsageAsync(sessionId, _lifetime.Token);
            _launchedTasks.Add(_pendingUsagePollTask);
        }
        catch (OperationCanceledException)
        {
            // The session is being torn down — nothing to report.
        }
        catch (Exception exception)
        {
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = exception.Message });
        }
    }

    // AC-274: acquires the same gate a real turn's prompt does, so at most one session/prompt is ever
    // outstanding — the wire gives agent_message_chunk no turn id, so this is what stops the poll's own chunk
    // from ever being mistaken for (or mistaken as) a running turn's. A failed request must never disturb the
    // session: it simply leaves Status as it was. The chunk itself is parsed and turned into Status inside
    // _HandleNotification, not here — reading a shared buffer only after this await resolves would race the
    // notification pump, which runs on its own task and is not guaranteed to have processed the chunk yet just
    // because the correlated JSON-RPC reply already has.
    private async Task _PollContextUsageAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The gate is cancelled or already disposed: the session is being torn down while this poll was
            // queued behind the turn that launched it. Nothing to clean up — the flag below was never set —
            // and nothing worth reporting: usage is a nicety, and the session is on its way out.
            return;
        }

        _capturingUsageResponse = true;
        _usageCaptureDeadline = Environment.TickCount64 + UsageCaptureWindowMilliseconds;
        try
        {
            var prompt = new object[] { new { type = "text", text = "/usage" } };
            await _connection.SendRequestAsync("session/prompt", new { sessionId, prompt }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed /usage round must never disturb the session (AC-274) — Status simply stays as it was.
            // No chunk will ever arrive for this attempt, so nothing will clear the flag on its own; clear it
            // here (P0-1). Safe to do unconditionally on this path: a genuine RPC failure means no chunk for
            // this attempt is coming, so there is nothing left in flight to race.
            _capturingUsageResponse = false;
        }
        finally
        {
            _promptGate.Release();
        }
    }

    private void _EmitTurnCompleted(JsonElement promptResult)
    {
        var (stopReason, isError) = _MapStopReason(promptResult);
        _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = stopReason, Result = null, IsError = isError, StopReason = stopReason });
    }

    // D7, protocol §3/§12: the wire only ever carries end_turn | cancelled | refusal — completed/blocked/failed
    // are Kimi's internal SDK TurnEndReason names and never reach the client (events-map.ts folds them onto
    // these three before the PromptResponse settles). Kimi folds a genuinely failed turn onto the same end_turn
    // value a successful one gets, so IsError is only ever true for "refusal" — a known, honest limitation
    // (there is no wire signal that tells a failed turn apart from a successful one), not a bug to "fix" later.
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

        // session/cancel is a notification (protocol §7): the already-outstanding session/prompt settles later
        // with stopReason "cancelled", handled by _SendPromptAsync/_EmitTurnCompleted when it arrives.
        await _connection.SendNotificationAsync("session/cancel", new { sessionId }, cancellationToken).ConfigureAwait(false);

        // Per spec, every permission request still outstanding when a cancel goes out must be answered with
        // outcome "cancelled" — kimi does not fail these on its own, and an unanswered one blocks the agent
        // forever. P1-2: re-snapshot and keep draining rather than iterating a single snapshot once — kimi may
        // still send a request_permission for this turn while this loop is running (protocol §7.5), and a
        // single pass would leave it unanswered. The flag set above is what bounds this loop: a request arriving
        // now is answered where it is received and never lands here, so the dictionary can only shrink.
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

    // "Allow always" only ever means approve_always on this wire (D-permissions) — Kimi has no separate concept
    // for "always for this exact call" vs "always for this kind of call", so the two are indistinguishable at
    // the plugin level. Known contract boundary, not a bug to later "fix".
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

    // Reads the optionId from the offered "options" list by kind, rather than guessing a canonical id blindly:
    // the plan_review (plan_approve/plan_revise/plan_reject_and_exit) and AskUserQuestion (q{n}_*) namespaces use
    // different ids for the same kinds. Falls back to the canonical id only when the kind is genuinely absent.
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
        "allow_once" => "approve_once",
        "allow_always" => "approve_always",
        _ => "reject",
    };

    public async Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // AC-272: exactly three configIds exist on the wire; anything else would earn a -32602 from kimi, but
        // filtering client-side means a bad key never reaches the agent at all — assert nothing was written.
        if (Array.IndexOf(_ValidConfigIds, key) < 0 || _sessionId is not { Length: > 0 } sessionId)
        {
            return;
        }

        var result = await _connection.SendRequestAsync("session/set_config_option", new { sessionId, configId = key, value }, cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("configOptions", out var configOptions) && configOptions.ValueKind == JsonValueKind.Array)
        {
            _liveOptions = _BuildLiveOptions(configOptions);
        }
    }

    // The configOptions shape (protocol §8): [{type,id,name,category,currentValue,options:[{value,name,description}]}].
    // A shrinking set (e.g. "thinking" absent for a non-thinking model) simply produces fewer launch options —
    // nothing here assumes a fixed set of three.
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
                // D12: the process ended on its own — a crash, not our own dispose — rather than through
                // DisposeAsync. Emitting these two before completing the channel is the whole point: a bare
                // channel-complete would leave the UI with no reason the session stopped.
                _events.Writer.TryWrite(new PluginSessionError { SessionId = _sessionId, Message = "The kimi acp process ended unexpectedly." });
                _events.Writer.TryWrite(new PluginTurnCompleted { SessionId = _sessionId, Subtype = "error", Result = null, IsError = true });
            }

            _events.Writer.TryComplete();
        }
    }

    private void _HandleNotification(KimiNotification notification)
    {
        if (notification.Method != "session/update")
        {
            return;
        }

        // AC-274/P0-2: a /usage poll's reply is a plain agent_message_chunk, indistinguishable on the wire
        // from a real turn's chunk — and the poll's own continuation (which sets _capturingUsageResponse,
        // see _PollContextUsageAsync) can race ahead of this pump on a different thread-pool thread. Only
        // swallow a chunk that actually parses as the usage line: an ordinary turn chunk that happens to
        // arrive while the flag is set simply falls through to the mapper below untouched, so a race never
        // loses real transcript content.
        if (_capturingUsageResponse)
        {
            if (Environment.TickCount64 > _usageCaptureDeadline)
            {
                // The poll it was armed for is long over and nothing ever parsed as usage — a format kimi
                // changed, a localised reply. Disarm rather than stay armed for the rest of the session: a
                // still-set flag would eventually meet a genuine assistant message that happens to match the
                // usage pattern and swallow it, and a silently missing message is worse than a missing
                // percentage.
                _capturingUsageResponse = false;
            }
            else if (_TryExtractAgentMessageText(notification.Params, out var usageChunkText)
                && KimiUsageParser.ParseContextUsedPercent(usageChunkText) is { } contextUsedPercent)
            {
                _capturingUsageResponse = false;

                // RateLimits stays empty (D13): Kimi's ACP surface has no cost/quota concept, only token
                // counts — an empty list here is an honest "not applicable", not a missing feature.
                _status = new PluginSessionStatus(contextUsedPercent, RateLimits: []);
                return;
            }
        }

        // Claim and write under the same gate as the permission path (see _emitGate): the mapper decides which
        // side owns a toolCallId's one PluginToolUseRequested, and whoever wins must have it in the channel
        // before the other side can write anything about that id.
        KimiSessionUpdateMapResult result;
        lock (_emitGate)
        {
            result = _toolCallMapper.Map(notification.Params);
            foreach (var evt in result.Events)
            {
                _events.Writer.TryWrite(evt);
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

    private async Task _HandleServerRequestAsync(KimiServerRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "session/request_permission":
                await _SurfacePermissionAsync(request, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // D11: an unmodelled reverse-request (fs/read_text_file, fs/write_text_file — never sent since we
                // advertise fs.* as false, or any future method) gets a JSON-RPC error, not a made-up success the
                // agent would act on as if it were real.
                await _connection.RespondErrorAsync(request.Id, -32601, $"Cockpit does not support '{request.Method}' yet.", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task _SurfacePermissionAsync(KimiServerRequest request, CancellationToken cancellationToken)
    {
        if (!_TryGetNestedString(request.Params, "toolCall", "toolCallId", out var toolCallId))
        {
            // P1-1: a malformed request must not just be dropped — it is blocking (protocol §5), and a silent
            // return leaves the agent waiting forever with no card and no error to explain why.
            await _connection.RespondErrorAsync(request.Id, -32602, "session/request_permission is missing toolCall.toolCallId.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // A permission request that arrives once this turn is being cancelled is answered "cancelled" right here
        // and never tracked. Two reasons: the operator asked for the turn to stop, so putting a fresh card on
        // screen would be answering a question nobody wants; and the drain loop in InterruptAsync below only
        // terminates because nothing new can enter the dictionary while it runs — a child that keeps sending
        // requests would otherwise keep that loop going for as long as it cares to.
        if (_cancelling)
        {
            await _connection.RespondAsync(request.Id, new { outcome = new { outcome = "cancelled" } }, cancellationToken).ConfigureAwait(false);
            return;
        }

        // P1-9: caps concurrent outstanding permission requests — an unbounded _pendingApprovals dictionary is
        // an OOM vector on untrusted stdout (a runaway or malicious kimi process flooding
        // session/request_permission faster than the operator can ever answer).
        if (_pendingApprovals.Count >= MaxPendingApprovals)
        {
            await _connection.RespondErrorAsync(request.Id, -32000, $"Too many outstanding permission requests (max {MaxPendingApprovals}).", cancellationToken).ConfigureAwait(false);
            return;
        }

        var options = request.Params.TryGetProperty("options", out var optionsElement) ? optionsElement.Clone() : default;
        if (!_pendingApprovals.TryAdd(toolCallId, new KimiPendingApproval(request.Id, options)))
        {
            // P0-5: the child process reused a toolCallId that already has an outstanding approval. Overwriting
            // the entry would let this second request answer whichever card the operator is looking at for the
            // first (confused deputy) — reject it instead of resolving the wrong one.
            await _connection.RespondErrorAsync(request.Id, -32602, $"Duplicate session/request_permission for toolCallId '{toolCallId}'.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Kimi tunnels AskUserQuestion over this same request — there is no session/request_question (protocol
        // §5) — recognisable only by its toolCall.title being literally "AskUserQuestion". The narrow plugin
        // contract has no separate "question" event, so it still rides PluginPermissionRequested; the host
        // renders whatever ToolName/InputJson say.
        var toolName = _TryGetNestedString(request.Params, "toolCall", "title", out var title) ? title : "tool";

        // P1-3, trigger (c): without a prior PluginToolUseRequested for this same id, the permission card the
        // host renders has no matching tool call to attach its buttons to (D3) — emit one now if nothing else
        // already did. Both the check and the two writes happen under _emitGate, because "already did" is only
        // true once the other pump's event is actually in the channel: kimi sends the tool_call and the
        // permission request for the same id back to back, they arrive on two different pumps, and claiming the
        // id is a separate step from writing the event it produced. Without the gate the notification pump can
        // win the claim and still be beaten to the channel, and the host then sees the permission card before
        // the tool card it must hang on — the exact D3 regression.
        lock (_emitGate)
        {
            if (_toolCallMapper.EnsureToolUseRequested(toolCallId, _sessionId, toolName) is { } toolUseRequested)
            {
                _events.Writer.TryWrite(toolUseRequested);
            }

            var toolCallJson = request.Params.TryGetProperty("toolCall", out var toolCall) ? toolCall.GetRawText() : "{}";
            _events.Writer.TryWrite(new PluginPermissionRequested { SessionId = _sessionId, ToolUseId = toolCallId, ToolName = toolName, InputJson = toolCallJson });
        }
    }

    // AC-273: says out loud that this session's hidden briefing — a profile's identity, a project's instructions,
    // an embedded Autopilot run's CEO prompt — is not reaching Kimi. There is no route for it over ACP: the
    // adapter reads no systemPrompt/instructions parameter, the _meta it accepts on session/new is parsed and
    // then never read, and --agent-file lives on the v2 engine that kimi acp never reaches. The one text that
    // does land in the system prompt ($KIMI_CODE_HOME/AGENTS.md) is introduced by Kimi's own template as "not a
    // privileged instruction channel", and that variable also relocates credentials, mcp.json and the session
    // store — a config-tree migration, not a place to drop one file. So the capability stays unclaimed and the
    // gap is made visible instead: an identity that silently evaporates is worse than one the operator can see
    // did not apply.
    private void _ReportUnappliedSystemPrompt(IReadOnlyDictionary<string, string>? options)
    {
        if (_ResolveOption(options, WellKnownPluginSessionOptions.AppendSystemPrompt, fallback: null) is null)
        {
            return;
        }

        _events.Writer.TryWrite(new PluginSessionError
        {
            SessionId = _sessionId,
            Message = "This session carries a system prompt (a profile identity, a project's instructions, or an "
                + "embedded run's briefing), but Kimi Code has no way to receive one over ACP — it is not applied. "
                + "Put anything the agent must know in your first message instead.",
        });
    }

    // The variable families scrubbed from the child's environment above. Kept as a named predicate rather than a
    // prefix list inline, so the reason each family is here stays attached to it.
    private static bool _IsForeignAgentCredential(string key) =>
        key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("CLAUDE_AGENT_", StringComparison.OrdinalIgnoreCase);

    private string _ResolveProcessWorkingDirectory(string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            return workingDirectory;
        }

        return string.IsNullOrWhiteSpace(_config.WorkingDirectory) ? Environment.CurrentDirectory : _config.WorkingDirectory;
    }

    // The operator's per-session launch-option choice wins over the profile's own configured default — the same
    // precedence CliAgentConfig.ResolveOption applies for Codex; this plugin cannot reference that type, so it
    // keeps its own copy.
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

    // Mirrors the agent_message_chunk branch of KimiSessionUpdateMapper, but reads params.update directly rather
    // than going through the mapper — the usage-capture path (AC-274) needs the raw text, not a PluginSessionEvent.
    private static bool _TryExtractAgentMessageText(JsonElement notificationParams, out string text)
    {
        if (notificationParams.ValueKind == JsonValueKind.Object
            && notificationParams.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Object
            && _TryGetString(update, "sessionUpdate", out var discriminator) && discriminator == "agent_message_chunk"
            && update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
        {
            text = textProperty.GetString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
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
        _events.Writer.TryComplete();
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

        // P1-6: wait for the fire-and-forget prompt/usage-poll tasks to actually finish releasing _promptGate
        // before disposing it below — both already report their own failures through _events/Status, so any
        // exception surfacing here on the await is not acted on further, only kept from becoming an unobserved
        // task exception on whichever thread-pool thread eventually ran their continuation. Every task launched
        // this session is awaited, not just the most recent of each kind: nothing in the contract stops a caller
        // sending a second message before the first turn settles, and the overwritten task would then still hold
        // the gate while it is disposed underneath it.
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
