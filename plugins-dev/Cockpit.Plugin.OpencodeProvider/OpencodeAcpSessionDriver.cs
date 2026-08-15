using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: opencode.ai over ACP, same host/pump/permission architecture as KimiAcpSessionDriver — but every
// capability below was re-measured against a real opencode process rather than assumed from Kimi (usage/cost
// reporting, forced permission-ask, config-option validation, stop reasons); see the ticket for the measurements.
internal sealed class OpencodeAcpSessionDriver : IPluginSessionDriver
{
    private const string _ClientName = "Cockpit";
    private const string _ClientVersion = "1.0.0";

    // The most outstanding `session/request_permission`s tracked at once — exposed for tests. Same defensive
    // cap Kimi's driver uses: an OOM vector on untrusted stdout is the same shape regardless of which ACP
    // agent is on the other end of the pipe.
    internal const int MaxPendingApprovals = 500;

    // opencode's own "mode" configId has exactly two values (measured live: "build", "plan"); Cockpit's four
    // host-side modes collapse onto them, acceptEdits failing closed to "build" rather than a looser tier
    // opencode does not have — same fail-closed reasoning Kimi's own dictionary comment gives.
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

    // Serialises "claim this toolCallId's one PluginToolUseRequested" with "write it to the channel" — two
    // independent pump tasks can both touch the same id. Held only around sync writes, never across an
    // await, so it cannot deadlock the pumps against each other.
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

    // The configOptions snapshot, rebuilt from session/new|resume/set_config_option/config_option_update —
    // authoritative for LiveOptions and SetLiveOptionAsync's own validation. Volatile: written from three
    // different tasks (StartAsync, SetLiveOptionAsync, the notification pump).
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

    // The initialize response's `authMethods` (measured live: opencode advertises exactly one, "opencode-login")
    // — kept alongside AgentCapabilities for a future config-view/UI, same as Kimi's own field.
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

        // AC-783: force a permission-ask (or allow, for bypassPermissions) policy via the one mechanism
        // live-verified to reach opencode's permission engine regardless of the project's own opencode.json —
        // Cockpit's consent card is the permission surface for this session, not the CLI's own config.
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
                // session/resume, never session/load — load replays the whole history as session/update
                // notifications, doubling the transcript Cockpit already keeps itself (same as Kimi's driver).
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
            // AC-783: unlike Kimi, this does not match a specific "not authenticated" JSON-RPC code — that
            // code was never exercised live (free models need no auth). The agent's own error text is kept,
            // with a general actionable pointer appended instead of a guessed code.
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

    // The permission policy forced onto every spawn via OPENCODE_CONFIG_CONTENT (see StartAsync). Only
    // "bypassPermissions" allows everything — honouring that explicit choice; every other mode, including
    // none selected, defaults to asking, so criterion 3 holds regardless of the project's own config.
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

    // AC-783: the base ACP spec defines five stop reasons; only end_turn/cancelled were exercised live, the
    // rest mapped per spec text (unlike Kimi's own SDK, which folds everything onto end_turn/refusal/cancelled).
    // IsError is only true for "refusal" — a limit reached is not the same claim as a turn having failed.
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

        // Per spec, every outstanding permission request must be answered "cancelled" or the agent blocks
        // forever. Re-snapshot and keep draining — opencode may still send one while this loop runs; the
        // flag set above bounds it, since a new request is answered where it arrives, never here.
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

    // Reads the optionId from the offered "options" list by kind rather than guessing blindly — mirrors
    // Kimi's own reasoning, even though this session only observed one canonical set live.
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
        // AC-783: validate against the live configOptions snapshot rather than a hardcoded id list (Kimi's own
        // protocol has a fixed three; opencode's is not fixed). Only rejects when the snapshot is non-empty
        // and positively excludes the key — an empty snapshot is not evidence either way (see _ShouldAttemptModelSwitch).
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
            // AC-783: read directly, not through the mapper — a real structured usage figure per turn, unlike
            // Kimi. `cost` is also observed live but PluginSessionStatus has no field for a currency amount,
            // so it is read and dropped: an honest gap in the shared contract, not a missing feature here.
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

        // A request arriving while this turn is being cancelled is answered "cancelled" right here and never
        // tracked — a fresh card would be answering a question nobody wants.
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

    // AC-783: says out loud that this session's hidden briefing is not reaching opencode — the base ACP spec
    // defines no systemPrompt parameter, and opencode's only instructions channel (AGENTS.md) is an
    // operator-maintained file, not something this driver can write into per session (mirrors Kimi's AC-273).
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
