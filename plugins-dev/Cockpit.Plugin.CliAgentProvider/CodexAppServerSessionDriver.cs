using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// `IPluginSessionDriver` for Codex over the persistent `codex app-server` JSON-RPC protocol (#45 phase 3),
// replacing the headless `CliSubprocessPluginSessionDriver`. Unlike `codex exec`, the app-server sends a
// request per shell command/file edit and blocks the turn for the operator's answer — hence `SupportsPermissions`.
internal sealed class CodexAppServerSessionDriver : IPluginSessionDriver
{
    private const string _ClientName = "cockpit";
    private const string _ClientVersion = "1.0.0";

    // Option key for the per-session sandbox choice, declared by the plugin and rendered by the dialog.
    public const string SandboxOptionKey = "sandbox";

    // Option key for the per-session model override — also a live control (#45 D4).
    public const string ModelOptionKey = "model";

    // Option key for the reasoning-effort control — a live control (#45 D4) and, since AC-1101, also a per-session
    // start option, carried as `effort` on `turn/start`.
    public const string EffortOptionKey = "effort";

    // Option key for the approval-policy control — a live control (#45 D4 inc2) and, since AC-1101, also a
    // profile-level start option (never a per-spawn override, see `SpawnOptionOverrides.NeverOverridable`),
    // carried as `approvalPolicy` on `turn/start`.
    public const string ApprovalOptionKey = "approvalPolicy";

    // Codex's AskForApproval enum, the simple string form (the granular-object form is not modelled here). Public
    // (AC-1101) so the plugin registration can declare the same set as the profile-level Approval start option,
    // without a second copy drifting from this one.
    public static readonly IReadOnlyList<string> ApprovalChoices = ["untrusted", "on-request", "never"];

    private readonly CodexAppServerConnection _connection;
    private readonly CliAgentConfig _config;
    private readonly string _executablePath;
    private readonly Action<PluginModelObservation>? _recordObservation;
    private readonly PluginSessionEventPublisher _events = new();

    // itemId -> the server request's JSON-RPC id, so an operator's allow/deny can be routed back to the exact
    // approval the server is blocking on (RespondToPermissionAsync correlates on itemId, which is the tool-use id).
    private readonly ConcurrentDictionary<string, JsonElement> _pendingApprovals = new();
    private readonly TaskCompletionSource<string> _threadReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();

    private string? _model;

    // The reasoning-effort override (#45 D4, profile-level start option since AC-1101). Null until the profile's
    // own option or the operator's live switch sets one, so a turn that never touched it carries no effort and
    // Codex uses its own default rather than one this driver invented.
    private string? _effort;

    // The approval-policy override (#45 D4 inc2, profile-level start option since AC-1101), same shape as effort —
    // null until the profile's own option or the operator's live switch sets one, so Codex keeps its own default.
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

    // AC-126: turn/completed carries no "final text" field, unlike Claude's stream-json result — the answer
    // arrives only as item/agentMessage/delta chunks, folded here so LastAssistantText/Result aren't null.
    // Read/reset only from the single-threaded notification pump, so it needs no lock of its own.
    private readonly StringBuilder _turnText = new();
    private readonly Dictionary<string, PluginBackgroundTask> _children = new();

    public CodexAppServerSessionDriver(Func<ICliSubprocess> subprocessFactory, CliAgentConfig config, string executablePath, Action<PluginModelObservation>? recordObservation = null)
    {
        _connection = new CodexAppServerConnection(subprocessFactory());
        _config = config;
        _executablePath = executablePath;
        _recordObservation = recordObservation;
        _model = config.Model;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true)
    {
        SupportsEnvVars = true,
        // Codex spawns its app-server in the session's working directory and edits within that cwd, so an isolated
        // embedded run (Autopilot worktree) stays inside its worktree (AC-174).
        ConfinesFileAccessToWorkingDirectory = true,
    };

    public string? SessionId => _threadId;

    public int? ProcessId => _connection.ProcessId;

    public PluginSessionStatus? Status => _status;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    public async Task<PluginChildTranscriptPage> ReadChildTranscriptAsync(string agentId, string? runId, string? cursor, CancellationToken cancellationToken = default)
    {
        if (!_knownChildren.Any(child => child.AgentId == agentId && child.RunId == runId))
        {
            return PluginChildTranscriptPage.Unavailable("child does not belong to this owner/run");
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var metadata = await _connection.SendRequestAsync("thread/read", new { threadId = agentId, includeTurns = false }, deadline.Token).ConfigureAwait(false);
            if (!metadata.TryGetProperty("thread", out var thread) || _StringOrEmpty(thread, "id") != agentId)
            {
                return PluginChildTranscriptPage.Unavailable("provider returned a different thread");
            }
            var page = await _connection.SendRequestAsync("thread/items/list", new { threadId = agentId, cursor, limit = 64, sortDirection = "asc" }, deadline.Token).ConfigureAwait(false);
            var entries = new List<PluginChildTranscriptEntry>();
            if (!page.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return PluginChildTranscriptPage.Unavailable("unsupported history response");
            }
            foreach (var record in data.EnumerateArray().Take(64))
            {
                if (!record.TryGetProperty("item", out var item))
                {
                    continue;
                }
                var id = _StringOrEmpty(item, "id");
                var type = _StringOrEmpty(item, "type");
                var kind = type switch { "agentMessage" => "assistant", "userMessage" => "user", "reasoning" => "thinking", _ => "tool_result" };
                var text = type == "agentMessage" ? _StringOrEmpty(item, "text") : item.GetRawText();
                entries.Add(new(_StringOrEmpty(record, "turnId") + ":" + id, kind, text.Length > 16384 ? text[..16384] + "\n[Entry truncated]" : text));
            }
            return new(entries, _TryGetString(page, "nextCursor", out var next) ? next : null, entries.Count == 0 ? "No entries" : "Read-only");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PluginChildTranscriptPage.Unavailable("history request timed out");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or CodexAppServerException)
        {
            return PluginChildTranscriptPage.Unavailable("connection stopped or history is not supported");
        }
    }

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

        // A per-session option wins over the profile's config default; absent, derive from a delegated session's
        // permission ceiling (acceptEdits/bypass -> workspace-write) so a delegated Codex task can actually write
        // instead of stalling on an approval nobody can answer (AC-100/AC-112). danger-full-access is never derived.
        var sandbox = CliAgentConfig.ResolveOption(options, SandboxOptionKey, null)
            ?? CodexSandbox.ForCeiling(CliAgentConfig.ResolveOption(options, WellKnownPluginSessionOptions.PermissionMode, null))
            ?? _config.SandboxMode;
        var effectiveModel = CliAgentConfig.ResolveOption(options, ModelOptionKey, _model);
        _model = effectiveModel;
        _sandbox = sandbox;

        // AC-1101: the profile's own effort/approval choice must already be in force for the first turn a spawned
        // session sends, before any live-panel switch could carry it. Absent, both stay null and Codex keeps its own default.
        _effort = CliAgentConfig.ResolveOption(options, EffortOptionKey, null);
        _approval = CliAgentConfig.ResolveOption(options, ApprovalOptionKey, null);

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

        // #1105 C: an account-level read fills the bar before the first turn's own notification would. Fire-
        // and-forget on the driver's own lifetime, off the critical path to PluginSessionInitialized — a slow or
        // unresponsive app-server must not delay session start for this.
        _ = _PrefetchRateLimitsAsync(_lifetime.Token);

        // The live-control choices (#45 D4), resolved on this same handshaked connection — one round-trip, no
        // second process (unlike CodexModelCatalog, which has no running server to reuse). Best-effort: an
        // unreadable listing just leaves the model control on the current model; effort's fixed set needs no lookup.
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
            // A hidden per-session system prompt (AC-180, e.g. Autopilot's CEO brief) rides thread/start as
            // developerInstructions — a developer-role instruction over the base prompt, never a visible turn.
            // Sent only when set; null leaves the thread's own instructions untouched, like sandbox/model above.
            var developerInstructions = _NullIfBlank(CliAgentConfig.ResolveOption(options, WellKnownPluginSessionOptions.AppendSystemPrompt, null));
            var started = await _connection.SendRequestAsync("thread/start", new { cwd, sandbox = _NullIfBlank(sandbox), model = _NullIfBlank(effectiveModel), developerInstructions }, cancellationToken).ConfigureAwait(false);

            // The thread id may ride the reply or only the thread/started notification — take whichever arrives,
            // so the driver does not depend on which of the two carries it in a given Codex version.
            threadId = _ExtractThreadId(started) ?? await _threadReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _threadId = threadId;
        _threadReady.TrySetResult(threadId);
        _events.Publish(new PluginSessionInitialized { SessionId = threadId, Tools = [], Cwd = _workingDirectory });
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_threadId is not { Length: > 0 } threadId)
        {
            throw new InvalidOperationException($"{nameof(SendUserMessageAsync)} was called before the session started.");
        }

        // Fire-and-forget: turn/start's reply lands only when the turn ends, so awaiting it would block the
        // caller; output streams via the notification pump instead, closed by turn/completed. Live model/
        // effort/approval/sandbox (#45 D4) are captured on the caller's thread so same-thread switches are picked up next turn.
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
            _events.Publish(new PluginSessionError { SessionId = _threadId, Message = exception.Message });
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_threadId is { Length: > 0 } threadId && _currentTurnId is { Length: > 0 } turnId)
        {
            await _connection.SendRequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken).ConfigureAwait(false);
        }

        // AC-943: the app-server blocks on its own `item/*/requestApproval` request, so an unanswered approval
        // holds it open past the interrupt — decline whatever is still pending, same wire shape as `_RespondDecisionAsync`.
        foreach (var itemId in _pendingApprovals.Keys.ToList())
        {
            if (_pendingApprovals.TryRemove(itemId, out var requestId))
            {
                await _connection.RespondAsync(requestId, new { decision = "decline" }, cancellationToken).ConfigureAwait(false);
            }
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
            _events.TryComplete();
        }
    }

    private void _HandleNotification(CodexNotification notification)
    {
        var source = _StringOrEmpty(notification.Params, "threadId");
        var isChild = source.Length > 0 && source != _threadId;
        var parentTool = isChild ? source : null;
        switch (notification.Method)
        {
            case "thread/started":
                if (_TryGetNestedString(notification.Params, "thread", "id", out var threadId))
                {
                    _threadReady.TrySetResult(threadId);
                }

                break;

            case "turn/started":
                if (isChild)
                {
                    break;
                }
                // A new turn clears leftover usage so one that reports no tokenUsage carries none, rather than
                // double-counting the previous turn's totals. Same reason for resetting the text accumulator
                // (AC-126): a pure tool-use turn must not inherit the previous turn's answer as its Result.
                _lastTurnUsage = null;
                _turnText.Clear();
                if (_TryGetNestedString(notification.Params, "turn", "id", out var turnId))
                {
                    _currentTurnId = turnId;
                }

                break;

            case "item/agentMessage/delta":
                if (_TryGetString(notification.Params, "delta", out var delta))
                {
                    if (!isChild)
                    {
                        _turnText.Append(delta);
                    }

                    _events.Publish(new PluginAssistantTextDelta { SessionId = isChild ? source : _threadId, ParentToolUseId = parentTool, BlockIndex = 0, Text = delta });
                }

                break;

            // The reasoning trace (#45 D3) streams as a thinking block, dimmed/collapsed, separate from the
            // visible answer. Raw reasoning and its summary are distinct wire notifications kept in separate
            // blocks so, if Codex emits both, they never concatenate into one jumbled block.
            case "item/reasoning/textDelta":
                if (_TryGetString(notification.Params, "delta", out var reasoningText))
                {
                    _events.Publish(new PluginAssistantThinkingDelta { SessionId = isChild ? source : _threadId, ParentToolUseId = parentTool, BlockIndex = 0, Thinking = reasoningText });
                }

                break;

            case "item/reasoning/summaryTextDelta":
                if (_TryGetString(notification.Params, "delta", out var reasoningSummary))
                {
                    _events.Publish(new PluginAssistantThinkingDelta { SessionId = isChild ? source : _threadId, ParentToolUseId = parentTool, BlockIndex = 1, Thinking = reasoningSummary });
                }

                break;

            case "item/started":
                _HandleToolItem(notification.Params, completed: false);
                break;

            case "item/completed":
                _HandleToolItem(notification.Params, completed: true);
                break;

            case "turn/completed":
                if (isChild)
                {
                    var childStatus = _TryGetNestedString(notification.Params, "turn", "status", out var terminal) ? terminal : "unknown";
                    _FinishChild(source, childStatus);
                }
                else
                {
                    _HandleTurnCompleted(notification.Params);
                }
                break;

            case "thread/tokenUsage/updated":
                if (isChild)
                {
                    if (_children.TryGetValue(source, out var child))
                    {
                        var usage = _ObjectOrDefault(notification.Params, "tokenUsage");
                        _children[source] = child with { Tokens = _TryGetLong(_ObjectOrDefault(usage, "total"), "totalTokens") ?? child.Tokens };
                        _PublishChildren();
                    }
                }
                else
                {
                    _HandleTokenUsage(notification.Params);
                }
                break;

            case "account/rateLimits/updated":
                _HandleRateLimits(notification.Params);
                break;

            case "error":
                if (isChild)
                {
                    _FinishChild(source, "failed");
                    break;
                }
                if (_IsRateLimitRefusal(notification.Params))
                {
                    _RecordObservation(PluginModelObservationKind.RateLimitRefusal);
                }

                _events.Publish(new PluginSessionError { SessionId = _threadId, Message = _ExtractErrorMessage(notification.Params) });
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

        var source = _StringOrEmpty(parameters, "threadId");
        var isChild = source.Length > 0 && source != _threadId;
        var sessionId = isChild ? source : _threadId;
        var parentTool = isChild ? source : null;
        if (itemType == "subAgentActivity")
        {
            var childId = _StringOrEmpty(item, "agentThreadId");
            var kind = _StringOrEmpty(item, "kind");
            if (childId.Length == 0 || completed)
            {
                return;
            }

            if (kind == "started")
            {
                _children[childId] = new PluginBackgroundTask(childId, PluginBackgroundTaskKind.SubAgent, _StringOrEmpty(item, "agentPath"))
                {
                    AgentId = childId, ParentTaskId = source, ParentToolUseId = childId,
                    RunId = _StringOrEmpty(parameters, "turnId"),
                };
                _events.Publish(new PluginToolUseRequested { SessionId = sessionId, ToolUseId = childId, ToolName = "agent", InputJson = item.GetRawText() });
                _PublishChildren();
            }
            else if (kind is "completed" or "failed" or "stopped")
            {
                _FinishChild(childId, kind);
            }

            return;
        }

        if (isChild && _children.TryGetValue(source, out var active) && !completed)
        {
            _children[source] = active with { LastTool = itemType == "commandExecution" ? "shell" : _StringOrEmpty(item, "tool") };
            _PublishChildren();
        }

        switch (itemType)
        {
            case "commandExecution" when !completed:
                _events.Publish(new PluginToolUseRequested { SessionId = sessionId, ParentToolUseId = parentTool, ToolUseId = itemId, ToolName = "shell", InputJson = _RawOrEmpty(item, "command") });
                break;

            case "commandExecution" when completed:
                _events.Publish(new PluginToolResult { SessionId = sessionId, ParentToolUseId = parentTool, ToolUseId = itemId, Content = _StringOrEmpty(item, "aggregatedOutput"), IsError = _IsNonZeroExit(item) });
                break;

            case "mcpToolCall" when !completed:
                _events.Publish(new PluginToolUseRequested { SessionId = sessionId, ParentToolUseId = parentTool, ToolUseId = itemId, ToolName = _StringOrEmpty(item, "tool"), InputJson = _RawOrEmpty(item, "arguments") });
                break;

            case "mcpToolCall" when completed:
                _events.Publish(new PluginToolResult { SessionId = sessionId, ParentToolUseId = parentTool, ToolUseId = itemId, Content = _RawOrEmpty(item, "result"), IsError = item.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) });
                break;

            case "collabAgentToolCall":
            case "collabToolCall":
                var recipients = new List<string>();
                if (item.TryGetProperty("receiverThreadIds", out var receivers) && receivers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var receiver in receivers.EnumerateArray())
                    {
                        if (receiver.ValueKind != JsonValueKind.String || receiver.GetString() is not { Length: > 0 } childId)
                        {
                            continue;
                        }

                        recipients.Add(childId);
                    }
                }

                if (recipients.Count == 0 && sessionId is not null)
                {
                    recipients.Add(sessionId);
                }

                foreach (var recipient in recipients.Distinct())
                {
                    _events.Publish(completed
                        ? new PluginToolResult { SessionId = recipient, ParentToolUseId = recipient == _threadId ? null : recipient, ToolUseId = itemId, Content = _RawOrEmpty(item, "agentsStates"), IsError = _StringOrEmpty(item, "status") == "failed" }
                        : new PluginToolUseRequested { SessionId = recipient, ParentToolUseId = recipient == _threadId ? null : recipient, ToolUseId = itemId, ToolName = _StringOrEmpty(item, "tool"), InputJson = item.GetRawText() });
                }
                break;
        }
    }

    private readonly Dictionary<string, PluginBackgroundTask> _childHistory = new();
    private readonly Dictionary<string, long> _childHistoryOrder = new();
    private long _childHistorySequence;
    private IReadOnlyList<PluginBackgroundTask> _knownChildren = [];

    private void _PublishChildren()
    {
        foreach (var child in _childHistory.Values.ToArray())
        {
            if (!_children.ContainsKey(child.TaskId) && child.State == "running")
            {
                _childHistory[child.TaskId] = child with { State = "unknown" };
            }
        }
        foreach (var child in _children.Values)
        {
            if (!_childHistory.TryGetValue(child.TaskId, out var known) || known.RunId != child.RunId || known.State != "running")
            {
                _childHistoryOrder[child.TaskId] = ++_childHistorySequence;
            }
            _childHistory[child.TaskId] = child;
        }
        while (_childHistory.Count > 128)
        {
            var oldest = _childHistory.Values.Where(child => !_children.ContainsKey(child.TaskId)).MinBy(child => _childHistoryOrder.GetValueOrDefault(child.TaskId));
            if (oldest is null)
            {
                break;
            }
            _childHistory.Remove(oldest.TaskId);
            _childHistoryOrder.Remove(oldest.TaskId);
        }
        _knownChildren = [.. _childHistory.Values];
        _events.Publish(new PluginBackgroundTasksChanged { SessionId = _threadId, Tasks = [.. _children.Values], Children = _knownChildren });
    }

    private void _FinishChild(string childId, string state)
    {
        if (_children.Remove(childId, out var child))
        {
            _childHistory[childId] = child with { State = state };
            _events.Publish(new PluginToolResult { SessionId = _threadId, ToolUseId = childId, Content = "Agent finished", IsError = false });
            _PublishChildren();
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
        if (isInterrupted || isError)
        {
            _children.Clear();
            _PublishChildren();
        }
        if (isError && _IsRateLimitRefusal(parameters))
        {
            _RecordObservation(PluginModelObservationKind.RateLimitRefusal);
        }
        // AC-126: the text this turn's agentMessage deltas accumulated (or null for a turn that produced none —
        // pure tool-use, or a failed turn with no answer), not the hardcoded null this used to carry.
        _events.Publish(new PluginTurnCompleted
        {
            SessionId = _threadId,
            Subtype = status,
            Result = _turnText.Length > 0 ? _turnText.ToString() : null,
            IsError = isError,
            StopReason = isInterrupted ? "interrupt" : null,
            Usage = _lastTurnUsage,
        });
        _currentTurnId = null;

        // Belt-and-braces alongside the turn/started reset above: a missed turn/started (an app-server build that
        // skips it, a resume replay) or a duplicate turn/completed must not hand this turn's text to the next one.
        _turnText.Clear();
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
        _rateLimits = CodexRateLimits.Parse(parameters);
        foreach (var window in _rateLimits)
        {
            _RecordObservation(PluginModelObservationKind.QuotaReading, window);
        }
        _PublishStatus();
    }

    // #1105 C: account/rateLimits/read answers with the same shape the notification's params carry, so it runs
    // through the same handler. Best-effort — a failure just leaves the bar empty until the first turn's own
    // notification fills it, today's behaviour.
    private async Task _PrefetchRateLimitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _connection.SendRequestAsync("account/rateLimits/read", new { }, cancellationToken).ConfigureAwait(false);
            _HandleRateLimits(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort, see remarks above.
        }
    }

    private void _PublishStatus()
    {
        var status = new PluginSessionStatus(_contextUsedPercent, _rateLimits);
        _status = status.HasAny ? status : null;
    }

    private void _RecordObservation(PluginModelObservationKind kind, PluginRateLimitWindow? quota = null) =>
        _recordObservation?.Invoke(new PluginModelObservation("cli-agent-provider.codex", kind, DateTimeOffset.UtcNow)
        {
            Model = _model,
            Quota = quota,
            Profile = _config.ProfileKey,
        });

    private static bool _IsRateLimitRefusal(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object
        && (parameters.GetRawText().Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || parameters.GetRawText().Contains("rate limit", StringComparison.OrdinalIgnoreCase));

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
                // Approval/input kinds not yet modeled (permissions profiles, tool user-input, MCP elicitations
                // — increment 2) each expect their own response shape, not a {decision}. A JSON-RPC error is
                // the only reply valid for all of them, unblocking the request without a guessed result shape.
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
        _events.Publish(new PluginPermissionRequested
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

        // Effort's choices are the selected model's own `supportedReasoningEfforts` (AC-1101) — sol/terra offer
        // "ultra", others do not. A model the listing has nothing for leaves the control out entirely, same as Model above.
        var effortChoices = models.ReasoningEffortsFor(_model);
        if (effortChoices.Count > 0)
        {
            options.Add(new PluginSessionLaunchOption(EffortOptionKey, "Effort", effortChoices, _effort));
        }

        // Approval has no current value until the operator picks one (Codex runs its own default), so it opens unset.
        options.Add(new PluginSessionLaunchOption(ApprovalOptionKey, "Approval", ApprovalChoices, _approval));
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

        _lifetime.Dispose();
    }
}
