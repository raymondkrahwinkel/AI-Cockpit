using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// <see cref="IPluginSessionDriver"/> for Codex over the persistent <c>codex app-server</c> JSON-RPC protocol
/// (#45 fase 3) — the interactive route that replaces the headless <see cref="CliSubprocessPluginSessionDriver"/>.
/// Unlike <c>codex exec</c>, the app-server can show a real approval dialog: it sends the client a request per
/// shell command / file edit and blocks the turn until the operator answers, which is why this driver reports
/// <see cref="PluginSessionCapabilities.SupportsPermissions"/> where the exec driver could not.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="StartAsync(string?, string?, string?, IReadOnlyDictionary{string, string}?, IReadOnlyList{PluginMcpServer}?, CancellationToken)"/>
/// spawns one long-lived process, does the <c>initialize</c>/<c>initialized</c> handshake, then <c>thread/start</c> (with the cwd the
/// cockpit already knows, #45 D5) or <c>thread/resume</c>. The thread id comes from the start reply or the
/// <c>thread/started</c> notification, whichever carries it. Each <see cref="SendUserMessageAsync"/> is a
/// <c>turn/start</c>; the streaming <c>item/*</c> and <c>turn/*</c> notifications are mapped to plugin events
/// by the notification pump, and the server's approval requests are surfaced as
/// <see cref="PluginPermissionRequested"/> and answered by <see cref="RespondToPermissionAsync"/>.
/// </remarks>
internal sealed class CodexAppServerSessionDriver : IPluginSessionDriver
{
    private const string _ClientName = "cockpit";
    private const string _ClientVersion = "1.0.0";

    /// <summary>Option key for the per-session sandbox choice, declared by the plugin and rendered by the dialog.</summary>
    public const string SandboxOptionKey = "sandbox";

    /// <summary>Option key for the per-session model override — also a live control (#45 D4).</summary>
    public const string ModelOptionKey = "model";

    /// <summary>Option key for the live reasoning-effort control (#45 D4), carried as <c>effort</c> on <c>turn/start</c>.</summary>
    public const string EffortOptionKey = "effort";

    /// <summary>Option key for the live approval-policy control (#45 D4 inc2), carried as <c>approvalPolicy</c> on <c>turn/start</c>.</summary>
    public const string ApprovalOptionKey = "approvalPolicy";

    // Codex's ReasoningEffort values — a fixed set, unlike the model list, so they need no live lookup.
    private static readonly IReadOnlyList<string> _EffortChoices = ["low", "medium", "high"];

    // Codex's AskForApproval enum, the simple string form (the granular-object form is not modelled here).
    private static readonly IReadOnlyList<string> _ApprovalChoices = ["untrusted", "on-request", "never"];

    private readonly CodexAppServerConnection _connection;
    private readonly CliAgentConfig _config;
    private readonly string _executablePath;
    private readonly Channel<PluginSessionEvent> _events = Channel.CreateUnbounded<PluginSessionEvent>();

    // itemId -> the server request's JSON-RPC id, so an operator's allow/deny can be routed back to the exact
    // approval the server is blocking on (RespondToPermissionAsync correlates on itemId, which is the tool-use id).
    private readonly ConcurrentDictionary<string, JsonElement> _pendingApprovals = new();
    private readonly TaskCompletionSource<string> _threadReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();

    private string? _model;

    // The live reasoning-effort override (#45 D4). Null until the operator picks one, so a turn that never touched
    // it carries no effort and Codex uses its own default rather than one this driver invented.
    private string? _effort;

    // The live approval-policy override (#45 D4 inc2), same shape as effort — null until picked, so Codex keeps its own default.
    private string? _approval;

    // The live sandbox override (#45 D4 inc2b), the kebab choice the operator picked. Pre-filled from the launch
    // sandbox (there is always one in effect), like the model — so the panel opens on the active sandbox rather than
    // blank, and each turn re-asserts it as a SandboxPolicy object.
    private string? _sandbox;
    private IReadOnlyDictionary<string, string>? _profileEnvironment;

    // The controls this session can switch mid-conversation (#45 D4), built once at start from the model listing
    // and the effective model. Set on the starting thread before StartAsync returns and only read afterwards, so
    // it needs no synchronisation of its own — the same publish-on-start shape the host reads Capabilities with.
    private IReadOnlyList<PluginSessionLaunchOption> _liveOptions = [];

    private string? _threadId;
    private string? _currentTurnId;
    private string? _workingDirectory;
    private Task? _notificationPump;
    private Task? _serverRequestPump;

    // The limits feed (#45 D7). The pump thread is the only writer of the three component fields; the immutable
    // snapshot it builds is published to the volatile field so the host's poll (a different thread) reads a
    // consistent value. Context and rate-limits arrive in separate notifications, so each is kept and recombined.
    private volatile PluginSessionStatus? _status;
    private double? _contextUsedPercent;
    private IReadOnlyList<PluginRateLimitWindow> _rateLimits = [];

    // The most recent turn's token breakdown (#45 D3), attached to the next turn/completed so the host's token
    // meter folds it in. Updated by thread/tokenUsage/updated, which arrives around the end of the turn.
    private PluginTokenUsage? _lastTurnUsage;

    public CodexAppServerSessionDriver(Func<ICliSubprocess> subprocessFactory, CliAgentConfig config, string executablePath)
    {
        _connection = new CodexAppServerConnection(subprocessFactory());
        _config = config;
        _executablePath = executablePath;
        _model = config.Model;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true)
    {
        SupportsEnvVars = true,
    };

    public string? SessionId => _threadId;

    public int? ProcessId => _connection.ProcessId;

    public PluginSessionStatus? Status => _status;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

    // The environment-carrying overload (AC-22): the profile's variables arrive host-scrubbed; stash them so the
    // spawn below lays them under the config's own variables (auth env-var, CODEX_HOME), which keep the last word.
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

        // A per-session option the operator picked in the New-session dialog wins over the profile's config
        // default; absent, the config value (and then the CLI's own default) applies.
        var sandbox = CliAgentConfig.ResolveOption(options, SandboxOptionKey, _config.SandboxMode);
        var effectiveModel = CliAgentConfig.ResolveOption(options, ModelOptionKey, _model);
        _model = effectiveModel;
        _sandbox = sandbox;

        // The session's MCP servers (#26) become -c config overrides on the app-server spawn; any bearer token
        // rides the process environment, never the command line (see CodexMcpConfig).
        var mcpLaunch = CodexMcpConfig.Build(mcpServers);
        var environmentVariables = _config.BuildEnvironmentVariables();

        // The profile's own variables (AC-22) lay under everything the driver sets itself: the config's auth
        // env-var/CODEX_HOME and the MCP bearer tokens keep the last word, so an operator variable cannot
        // redirect the CLI's home or clobber a session credential.
        foreach (var (key, value) in _profileEnvironment ?? new Dictionary<string, string>())
        {
            if (!environmentVariables.ContainsKey(key))
            {
                environmentVariables[key] = value;
            }
        }

        foreach (var (key, value) in mcpLaunch.EnvironmentVariables)
        {
            environmentVariables[key] = value;
        }

        _workingDirectory = _ResolveProcessWorkingDirectory(workingDirectory);
        _connection.Start(_executablePath, _workingDirectory, environmentVariables, mcpLaunch.ConfigArgs);
        _notificationPump = Task.Run(() => _PumpNotificationsAsync(_lifetime.Token), CancellationToken.None);
        _serverRequestPump = Task.Run(() => _PumpServerRequestsAsync(_lifetime.Token), CancellationToken.None);

        await _connection.SendRequestAsync("initialize", new { clientInfo = new { name = _ClientName, version = _ClientVersion } }, cancellationToken).ConfigureAwait(false);
        await _connection.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);

        // The live-control choices (#45 D4) — resolved on this same handshaked connection, so listing the models
        // costs one round-trip and no second process (unlike the New-session dialog's CodexModelCatalog, which has
        // no running server to reuse). Best-effort: an unreadable listing leaves the model control on the current
        // model alone, and effort's fixed set needs no lookup at all.
        _liveOptions = _BuildLiveOptions(await _ListLiveModelsAsync(cancellationToken).ConfigureAwait(false));

        var cwd = _NullIfBlank(workingDirectory);
        string threadId;
        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            var resumed = await _connection.SendRequestAsync("thread/resume", new { threadId = resumeSessionId, cwd }, cancellationToken).ConfigureAwait(false);
            threadId = _ExtractThreadId(resumed) ?? resumeSessionId;
        }
        else
        {
            var started = await _connection.SendRequestAsync("thread/start", new { cwd, sandbox = _NullIfBlank(sandbox), model = _NullIfBlank(effectiveModel) }, cancellationToken).ConfigureAwait(false);

            // The thread id may ride the reply or only the thread/started notification — take whichever arrives,
            // so the driver does not depend on which of the two carries it in a given Codex version.
            threadId = _ExtractThreadId(started) ?? await _threadReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _threadId = threadId;
        _threadReady.TrySetResult(threadId);
        _events.Writer.TryWrite(new PluginSessionInitialized { SessionId = threadId, Tools = [], Cwd = _workingDirectory });
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_threadId is not { Length: > 0 } threadId)
        {
            throw new InvalidOperationException($"{nameof(SendUserMessageAsync)} was called before the session started.");
        }

        // Fire-and-forget: turn/start's reply lands only when the turn ends, so awaiting it here would block the
        // caller for the whole turn. The turn's output streams through the notification pump instead, closed by
        // turn/completed — mirroring how the exec driver runs its turn in the background.
        // The live model/effort/approval/sandbox (#45 D4) are captured here, on the caller's thread, so a switch the
        // operator makes through SetLiveOptionAsync (same thread) is picked up by the next turn and never read mid-write.
        var input = new object[] { new { type = "text", text } };
        _ = _SendTurnAsync(threadId, input, _model, _effort, _approval, _sandbox, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task _SendTurnAsync(string threadId, object[] input, string? model, string? effort, string? approval, string? sandbox, CancellationToken cancellationToken)
    {
        try
        {
            // model/effort/approvalPolicy/sandboxPolicy are per-turn overrides (#45 D4): TurnStartParams takes them all
            // as optional, so a null simply leaves the thread's own default in place. sandboxPolicy is the tagged-union
            // object keyed by its camelCase type (unlike thread/start's SandboxMode string), built from the kebab choice.
            var sandboxPolicy = CodexSandbox.ToPolicyType(sandbox) is { } policyType ? new { type = policyType } : null;
            await _connection.SendRequestAsync("turn/start", new { threadId, input, model = _NullIfBlank(model), effort = _NullIfBlank(effort), approvalPolicy = _NullIfBlank(approval), sandboxPolicy }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The session is being torn down — nothing to report.
        }
        catch (Exception exception)
        {
            _events.Writer.TryWrite(new PluginSessionError { SessionId = _threadId, Message = exception.Message });
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_threadId is { Length: > 0 } threadId && _currentTurnId is { Length: > 0 } turnId)
        {
            await _connection.SendRequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // Store only — the new value rides the next turn/start (_SendTurnAsync captures it), so there is nothing to
        // send to the server now. A key this driver did not declare is ignored: the host renders exactly the options
        // LiveOptions reported, so an unknown key is contract drift, not an operator mistake to surface mid-session.
        switch (key)
        {
            case ModelOptionKey:
                _model = _NullIfBlank(value);
                break;

            case EffortOptionKey:
                _effort = _NullIfBlank(value);
                break;

            case ApprovalOptionKey:
                _approval = _NullIfBlank(value);
                break;

            case SandboxOptionKey:
                _sandbox = _NullIfBlank(value);
                break;
        }

        return Task.CompletedTask;
    }

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        _RespondDecisionAsync(toolUseId, allow ? "accept" : "decline", cancellationToken);

    // "Allow always" is Codex's acceptForSession (D4): the agent stops asking for the like of this call for the
    // rest of the thread, where a plain accept clears only this one prompt. Both the command-execution and
    // file-change approval responses accept it (verified against the generated schema).
    public Task AllowPermissionAlwaysAsync(string toolUseId, CancellationToken cancellationToken = default) =>
        _RespondDecisionAsync(toolUseId, "acceptForSession", cancellationToken);

    private async Task _RespondDecisionAsync(string toolUseId, string decision, CancellationToken cancellationToken)
    {
        if (_pendingApprovals.TryRemove(toolUseId, out var requestId))
        {
            await _connection.RespondAsync(requestId, new { decision }, cancellationToken).ConfigureAwait(false);
        }
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
            _events.Writer.TryComplete();
        }
    }

    private void _HandleNotification(CodexNotification notification)
    {
        switch (notification.Method)
        {
            case "thread/started":
                if (_TryGetNestedString(notification.Params, "thread", "id", out var threadId))
                {
                    _threadReady.TrySetResult(threadId);
                }

                break;

            case "turn/started":
                // A new turn starts fresh on usage: clear any leftover so a turn that reports no tokenUsage carries
                // none, rather than the previous turn's totals leaking into it and double-counting in the meter.
                _lastTurnUsage = null;
                if (_TryGetNestedString(notification.Params, "turn", "id", out var turnId))
                {
                    _currentTurnId = turnId;
                }

                break;

            case "item/agentMessage/delta":
                if (_TryGetString(notification.Params, "delta", out var delta))
                {
                    _events.Writer.TryWrite(new PluginAssistantTextDelta { SessionId = _threadId, BlockIndex = 0, Text = delta });
                }

                break;

            // The reasoning trace (#45 D3) — streamed as a thinking block so the host renders it dimmed/collapsed,
            // separate from the visible answer. The raw reasoning and its summary are distinct wire notifications
            // with their own content; they go to separate blocks so that, if Codex emits both, the two never
            // concatenate into one jumbled block.
            case "item/reasoning/textDelta":
                if (_TryGetString(notification.Params, "delta", out var reasoningText))
                {
                    _events.Writer.TryWrite(new PluginAssistantThinkingDelta { SessionId = _threadId, BlockIndex = 0, Thinking = reasoningText });
                }

                break;

            case "item/reasoning/summaryTextDelta":
                if (_TryGetString(notification.Params, "delta", out var reasoningSummary))
                {
                    _events.Writer.TryWrite(new PluginAssistantThinkingDelta { SessionId = _threadId, BlockIndex = 1, Thinking = reasoningSummary });
                }

                break;

            case "item/started":
                _HandleToolItem(notification.Params, completed: false);
                break;

            case "item/completed":
                _HandleToolItem(notification.Params, completed: true);
                break;

            case "turn/completed":
                _HandleTurnCompleted(notification.Params);
                break;

            case "thread/tokenUsage/updated":
                _HandleTokenUsage(notification.Params);
                break;

            case "account/rateLimits/updated":
                _HandleRateLimits(notification.Params);
                break;

            case "error":
                _events.Writer.TryWrite(new PluginSessionError { SessionId = _threadId, Message = _ExtractErrorMessage(notification.Params) });
                break;
        }
    }

    private void _HandleToolItem(JsonElement parameters, bool completed)
    {
        if (!parameters.TryGetProperty("item", out var item)
            || !_TryGetString(item, "type", out var itemType)
            || !_TryGetString(item, "id", out var itemId))
        {
            return;
        }

        switch (itemType)
        {
            case "commandExecution" when !completed:
                _events.Writer.TryWrite(new PluginToolUseRequested { SessionId = _threadId, ToolUseId = itemId, ToolName = "shell", InputJson = _RawOrEmpty(item, "command") });
                break;

            case "commandExecution" when completed:
                _events.Writer.TryWrite(new PluginToolResult { SessionId = _threadId, ToolUseId = itemId, Content = _StringOrEmpty(item, "aggregatedOutput"), IsError = _IsNonZeroExit(item) });
                break;

            case "mcpToolCall" when !completed:
                _events.Writer.TryWrite(new PluginToolUseRequested { SessionId = _threadId, ToolUseId = itemId, ToolName = _StringOrEmpty(item, "tool"), InputJson = _RawOrEmpty(item, "arguments") });
                break;

            case "mcpToolCall" when completed:
                _events.Writer.TryWrite(new PluginToolResult { SessionId = _threadId, ToolUseId = itemId, Content = _RawOrEmpty(item, "result"), IsError = item.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) });
                break;
        }
    }

    private void _HandleTurnCompleted(JsonElement parameters)
    {
        // TurnStatus is completed|interrupted|failed|inProgress. An interrupt is the operator's own deliberate
        // stop, not a failure — mark it IsError:false with StopReason "interrupt" so the UI does not render
        // "Turn failed (interrupted)", matching CliSubprocessPluginSessionDriver's handling of the same case.
        var status = _TryGetNestedString(parameters, "turn", "status", out var turnStatus) ? turnStatus : "completed";
        var isInterrupted = string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase);
        var isError = !isInterrupted && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);
        _events.Writer.TryWrite(new PluginTurnCompleted
        {
            SessionId = _threadId,
            Subtype = status,
            Result = null,
            IsError = isError,
            StopReason = isInterrupted ? "interrupt" : null,
            Usage = _lastTurnUsage,
        });
        _currentTurnId = null;
    }

    // thread/tokenUsage/updated carries how full the context window is: the last turn's footprint (falling back to
    // the thread total) over the model's window. "last" is the most recent turn's usage, which is the running
    // context going into the next turn — the closest analogue to Claude's context_window.used_percentage.
    private void _HandleTokenUsage(JsonElement parameters)
    {
        // Guard the entry, not just the nested reads: a notification with no "params" reaches here as
        // default(JsonElement), and TryGetProperty on a non-object throws — which would kill the whole pump.
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // The last turn's breakdown feeds the host's token meter (#45 D3); kept for the next turn/completed to carry.
        _lastTurnUsage = _ParseTurnUsage(_ObjectOrDefault(usage, "last")) ?? _lastTurnUsage;

        var contextWindow = _TryGetLong(usage, "modelContextWindow");
        var usedTokens = _TryGetLong(_ObjectOrDefault(usage, "last"), "totalTokens")
            ?? _TryGetLong(_ObjectOrDefault(usage, "total"), "totalTokens");

        if (contextWindow is > 0 && usedTokens is not null)
        {
            _contextUsedPercent = Math.Clamp((double)usedTokens.Value / contextWindow.Value * 100, 0, 100);
            _PublishStatus();
        }
    }

    // Codex's TokenUsageBreakdown → the host's per-turn token counts. Reasoning output is folded into output
    // tokens (it is completion the turn produced); cached input maps to the cache-read bucket; Codex reports no
    // cache-creation count, so that stays zero.
    private static PluginTokenUsage? _ParseTurnUsage(JsonElement breakdown)
    {
        if (breakdown.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = _TryGetInt(breakdown, "inputTokens") ?? 0;
        var output = (_TryGetInt(breakdown, "outputTokens") ?? 0) + (_TryGetInt(breakdown, "reasoningOutputTokens") ?? 0);
        var cachedInput = _TryGetInt(breakdown, "cachedInputTokens") ?? 0;
        return new PluginTokenUsage(input, output, cachedInput, 0);
    }

    // account/rateLimits/updated carries the whole snapshot, so primary/secondary are replaced wholesale rather
    // than merged: a window the snapshot no longer reports is a window that no longer applies.
    private void _HandleRateLimits(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("rateLimits", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // The whole snapshot is replaced (not merged): a window it no longer reports is a window that no longer
        // applies. Order preserved as the provider gave it — the host renders the windows in that order.
        var windows = new List<PluginRateLimitWindow>(2);
        if (_ParseWindow(snapshot, "primary") is { } primary)
        {
            windows.Add(primary);
        }

        if (_ParseWindow(snapshot, "secondary") is { } secondary)
        {
            windows.Add(secondary);
        }

        _rateLimits = windows;
        _PublishStatus();
    }

    private void _PublishStatus()
    {
        var status = new PluginSessionStatus(_contextUsedPercent, _rateLimits);
        _status = status.HasAny ? status : null;
    }

    private static PluginRateLimitWindow? _ParseWindow(JsonElement snapshot, string property)
    {
        if (!snapshot.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object
            || _TryGetDouble(window, "usedPercent") is not { } usedPercent)
        {
            return null;
        }

        var windowMinutes = _TryGetInt(window, "windowDurationMins");
        var resetsAt = _TryGetLong(window, "resetsAt") is { } epochSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
            : (DateTimeOffset?)null;

        return new PluginRateLimitWindow(_WindowLabel(windowMinutes), usedPercent, resetsAt, windowMinutes);
    }

    // The provider owns the header label (#45 D7): derive it from the window's span so a five-hour window reads
    // "5h" and a weekly one "7d", and a window with no span falls back to a neutral "rate".
    private static string _WindowLabel(int? windowMinutes) => windowMinutes switch
    {
        null => "rate",
        < 60 => $"{windowMinutes}m",
        < 1440 => $"{windowMinutes / 60}h",
        _ => $"{windowMinutes / 1440}d",
    };

    private static int? _TryGetInt(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static long? _TryGetLong(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
            ? value
            : null;

    private static double? _TryGetDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;

    private static JsonElement _ObjectOrDefault(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.Object
            ? element
            : default;

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

    private async Task _HandleServerRequestAsync(CodexServerRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "item/commandExecution/requestApproval":
            case "item/fileChange/requestApproval":
                _SurfaceApproval(request);
                break;

            default:
                // Approval/input kinds we do not yet model (permissions profiles, tool user-input, MCP
                // elicitations — increment 2) each expect their own response shape ({permissions}, {answers},
                // {action}, …), not a {decision}. A JSON-RPC error is the only reply that is valid for all of
                // them: it unblocks the server's request without sending a result it could fail to deserialize.
                await _connection.RespondErrorAsync(request.Id, -32601, $"Cockpit does not support '{request.Method}' yet.", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void _SurfaceApproval(CodexServerRequest request)
    {
        if (!_TryGetString(request.Params, "itemId", out var itemId))
        {
            return;
        }

        _pendingApprovals[itemId] = request.Id;
        var isCommand = request.Method == "item/commandExecution/requestApproval";
        _events.Writer.TryWrite(new PluginPermissionRequested
        {
            SessionId = _threadId,
            ToolUseId = itemId,
            ToolName = isCommand ? "shell" : "apply_patch",
            InputJson = isCommand ? _RawOrEmpty(request.Params, "command") : request.Params.GetRawText(),
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

    private async Task<CodexModelListing> _ListLiveModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _connection.SendRequestAsync("model/list", new { }, cancellationToken).ConfigureAwait(false);
            return CodexModelCatalog.ParseListing(result);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled the whole start — let it unwind rather than swallowing it into an empty listing
            // and pressing on to thread/start with a dead token, the way the turn/pump paths in this file do.
            throw;
        }
        catch (Exception)
        {
            // Best-effort, as the New-session dialog's listing is: a codex that cannot list (an older build, a
            // transient failure) leaves the model control on the current model, which _BuildLiveOptions handles.
            return CodexModelListing.Empty;
        }
    }

    private IReadOnlyList<PluginSessionLaunchOption> _BuildLiveOptions(CodexModelListing models)
    {
        var options = new List<PluginSessionLaunchOption>(4);

        // Model: the live listing, with the current model guaranteed among the choices — a pinned model or alias the
        // listing omits still shows as the selected value rather than opening the panel blank, so CurrentValue is
        // always selectable. No model named at all (the CLI's own default) leaves the control out: nothing to switch.
        var modelChoices = new List<string>(models.Ids);
        if (_model is { Length: > 0 } current && !modelChoices.Contains(current))
        {
            modelChoices.Insert(0, current);
        }

        if (modelChoices.Count > 0)
        {
            options.Add(new PluginSessionLaunchOption(ModelOptionKey, "Model", modelChoices, _model));
        }

        // Sandbox opens on the active launch sandbox (there is always one), like the model — the same kebab choices
        // the New-session dialog offers, which the driver turns into the SandboxPolicy object for the wire.
        options.Add(new PluginSessionLaunchOption(SandboxOptionKey, "Sandbox", CodexSandbox.Choices, _sandbox));

        // Effort and approval have no current value until the operator picks one (Codex runs its own default), so
        // they open unset.
        options.Add(new PluginSessionLaunchOption(EffortOptionKey, "Effort", _EffortChoices, _effort));
        options.Add(new PluginSessionLaunchOption(ApprovalOptionKey, "Approval", _ApprovalChoices, _approval));
        return options;
    }

    private static string? _ExtractThreadId(JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (_TryGetString(result, "threadId", out var direct))
        {
            return direct;
        }

        return _TryGetNestedString(result, "thread", "id", out var nested) ? nested : null;
    }

    private static string _ExtractErrorMessage(JsonElement parameters)
    {
        if (_TryGetNestedString(parameters, "error", "message", out var message))
        {
            return message;
        }

        return _TryGetString(parameters, "error", out var raw) ? raw : "codex app-server reported an error.";
    }

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

    private static bool _TryGetNestedString(JsonElement parent, string outerProperty, string innerProperty, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(outerProperty, out var outer))
        {
            return _TryGetString(outer, innerProperty, out value);
        }

        value = string.Empty;
        return false;
    }

    private static string _StringOrEmpty(JsonElement parent, string property) =>
        _TryGetString(parent, property, out var value) ? value : string.Empty;

    private static string _RawOrEmpty(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? element.GetRawText()
            : string.Empty;

    private static bool _IsNonZeroExit(JsonElement item) =>
        item.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code) && code != 0;

    private static string? _NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public async ValueTask DisposeAsync()
    {
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

        _lifetime.Dispose();
    }
}
