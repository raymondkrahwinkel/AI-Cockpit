using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// `IPluginSessionDriver` for the headless `claude` CLI over the persistent stream-json protocol
// (Fase 4, SDK route, weg A) — the plugin-owned analogue of the host's `ClaudeCliSession`. One long-lived process
// hosts the whole multi-turn conversation; each `SendUserMessageAsync` writes one user-message line and the
// streamed `assistant`/`stream_event`/`result` lines are mapped to plugin events by
// `ClaudeStreamJson`.
// Permissions ride the *control protocol*, not an HTTP MCP server (`ClaudeControlProtocol`): the CLI
// is spawned without `--permission-prompt-tool`, an `initialize` control_request puts the cockpit on the
// control channel, and every tool needing approval arrives as a `can_use_tool` control_request which this driver
// surfaces as `PluginPermissionRequested` and answers with a control_response — the exact same in-band
// shape Codex's app-server route uses for `item/*/requestApproval`. No logged-in `claude` exists in this
// sandbox, so the live end (the CLI emitting `can_use_tool` for this spawn) needs a manual eyeball check; the
// turn-taking and the parse/respond round-trip are unit-tested against a fake subprocess.
internal sealed class ClaudeSdkSessionDriver : IPluginSessionDriver
{
    // Option key for the per-session permission mode (also a live control) — the well-known key the host's driver
    // adapter folds its typed permission-mode selection into, so a launch-time choice (bypass, plan, …) actually
    // reaches this driver instead of falling back to the default.
    public const string PermissionModeOptionKey = WellKnownPluginSessionOptions.PermissionMode;

    // Option key for the per-session model override (also a live control, #45 D4) — the well-known key the host adapter wires its live model switch to.
    public const string ModelOptionKey = WellKnownPluginSessionOptions.Model;

    // Option key for the per-session reasoning effort (a live control, #45 D4) — maps to the CLI's thinking-token budget, the one budget the control protocol can set mid-session (set_max_thinking_tokens).
    public const string EffortOptionKey = "effort";

    private readonly Func<IClaudeSdkSubprocess> _subprocessFactory;
    private readonly ClaudeProviderConfig _config;
    private readonly string _executablePath;
    private readonly PluginSessionEventPublisher _events = new();
    private readonly CancellationTokenSource _lifetime = new();

    // tool_use_id -> the pending can_use_tool request the CLI is blocking on. Keyed on tool_use_id because that is what
    // the transcript card (and therefore the UI's decision) is correlated on; the request_id is the wire correlation the
    // response must carry, and the original input must ride back verbatim as updatedInput on an allow — sending an empty
    // object there would make the CLI run the tool with no arguments (Bash with no command, Write with no content, …).
    private readonly ConcurrentDictionary<string, (string RequestId, string InputJson)> _pendingApprovals = new();

    // The limits feed (#45 D7, AC-530). Fed from the stdout pump and from the usage poll, and read by the host's
    // poll at each turn boundary; it publishes its own immutable snapshot and locks its own fields.
    private readonly ClaudeSdkUsage _usage = new();

    // request_id -> the awaiter for that reply. The fire-and-forget requests register none.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pendingControlResponses = new();

    // Ticks of the last successful `get_usage`. The context breakdown is not throttled — it is local, and it is
    // the one that changes every turn.
    private long _lastAllowancePollTicks;

    private IClaudeSdkSubprocess? _subprocess;
    private Task? _stdoutPump;
    private Task? _stderrDrain;
    private string? _sessionId;
    private string? _model;
    private string _effort = "medium";
    private IReadOnlyList<PluginSessionLaunchOption> _liveOptions = [];
    private IReadOnlyDictionary<string, string>? _profileEnvironment;

    // The per-session --mcp-config file this launch wrote (the shared registry fanned in) — written even as an
    // explicit empty file when an UNATTENDED launch resolved no registry servers (AC-378), so --strict-mcp-config
    // always has a config to pair with. Deleted on dispose: it can hold a user API-key server's bearer header, and a
    // config for a session that has ended is nobody's business (the TTY route hands the same file to the host to
    // clean up; the SDK driver owns its own process lifetime, so it owns this file's lifetime too).
    private string? _mcpConfigPath;

    // The per-session --append-system-prompt-file this launch wrote, on the same terms as the mcp-config above: it
    // carries the standing instruction the host composed (for the assistant, that is its memory and current state),
    // and it is deleted on dispose because a prompt for a session that has ended is nobody's business either.
    private string? _systemPromptPath;

    public ClaudeSdkSessionDriver(Func<IClaudeSdkSubprocess> subprocessFactory, ClaudeProviderConfig config, string executablePath)
    {
        _subprocessFactory = subprocessFactory;
        _config = config;
        _executablePath = executablePath;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(
        SupportsTools: true,
        SupportsPermissions: true,
        SupportsVision: true)
    {
        SupportsLiveModelSwitch = true,
        SupportsPermissionModeSwitch = true,
        SupportsEnvVars = true,
        // Claude spawns in the session's working directory and edits with cwd-bound native tools, so an isolated
        // embedded run (Autopilot worktree) stays inside its worktree (AC-174).
        ConfinesFileAccessToWorkingDirectory = true,
        // The CLI summarises its own conversation and carries on under the same session id (AC-664) — see
        // CompactContextAsync for the channel and what was measured.
        SupportsContextCompaction = true,
    };

    public string? SessionId => _sessionId;

    public int? ProcessId => _subprocess?.ProcessId;

    // AC-530: the context window and the rolling allowances, read off this session's own stdout. Null until the CLI
    // has reported at least one of them, which keeps the header's pill hidden rather than showing an invented zero.
    public PluginSessionStatus? Status => _usage.Status;

    public IReadOnlyList<PluginSessionLaunchOption> LiveOptions => _liveOptions;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Events;

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

    // The environment-carrying overload (AC-22): the profile's variables arrive host-scrubbed; stash them for
    // _BuildEnvironment, where the driver's own rules (ANTHROPIC_* drop, CLAUDE_CONFIG_DIR) keep the last word.
    public Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        _profileEnvironment = environment;
        return StartAsync(model, workingDirectory, resumeSessionId, options, mcpServers, cancellationToken);
    }

    public async Task StartAsync(string? model, string? workingDirectory, string? resumeSessionId, IReadOnlyDictionary<string, string>? options, IReadOnlyList<PluginMcpServer>? mcpServers, CancellationToken cancellationToken)
    {
        // A per-session option the operator picked in the New-session dialog wins over the model parameter, which wins
        // over the profile's own default; likewise the permission mode.
        var permissionMode = _ResolveOption(options, PermissionModeOptionKey, defaultValue: "default") ?? "default";
        var effectiveModel = _ResolveOption(options, ModelOptionKey, model);
        _model = effectiveModel;
        _effort = _ResolveOption(options, EffortOptionKey, _effort) ?? _effort;

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Canonicalise once (Path.GetFullPath) and spawn with that exact string — the TTY route (TtyLauncher) already
        // does, so without this the two routes handed claude the same folder spelled differently ("D:\Projects\…" vs
        // "D:/Projects/…"). claude keys per-project state (trust, MCP approvals) on the literal cwd string, so a split
        // spelling made a TTY and an SDK session land on two separate .claude.json project entries for one directory —
        // trust marked on one did not cover the other, and it doubled the write-contention on the shared file.
        var resolvedWorkingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory);
        var configJsonDirectory = ClaudeConfigPaths.ResolveConfigJsonDirectory(_config.ConfigDir, userHome);

        // Trust must land before the process starts, or the headless CLI blocks on its interactive trust dialog with
        // nothing able to answer it — in the .claude.json the CLI reads for this spawn.
        ClaudeWorkspaceTrust.MarkWorkingDirectoryTrusted(configJsonDirectory, resolvedWorkingDirectory);

        // Whether anyone is watching this session (AC-378): a delegated task and a self-driving embedded run are not,
        // a pane the operator opened is — including an interactive SDK pane, which is why this is not simply "am I
        // the SDK route". It decides both the strict flag below and whether an empty resolution still writes a file.
        //
        // Only an explicit "false" buys the additive behaviour. Absence means a host that does not state attendance
        // at all (one older than this split), and there the safe reading is unattended: strict, exactly as this route
        // behaved before. Reading absence as "attended" would instead hand a delegated agent the operator's own
        // claude.ai connectors on such a host — the very escalation AC-378 exists to prevent — and minHostVersion
        // cannot be leaned on to prevent that pairing, since PluginLoadPolicy only enforces it from host major 1.
        var unattended = !string.Equals(
            _ResolveOption(options, WellKnownPluginSessionOptions.Unattended, defaultValue: "true"),
            "false",
            StringComparison.OrdinalIgnoreCase);

        // Fan the shared MCP registry (already narrowed to this session's selection by the host adapter) into a
        // --mcp-config file. Unattended, this writes one even when the resolution produced no servers
        // (writeEmptyExplicit: true), because ClaudeSdkArguments.BuildArguments then pairs --mcp-config with
        // --strict-mcp-config: a null path would drop --mcp-config from the command line and let the CLI fall back
        // to its own user/project config instead of the empty set the resolution actually produced — exactly the
        // "narrowing to nothing looks like no narrowing" trap AC-378 closes. Attended, there is no strict flag to
        // pair with and an empty file would only strip the operator's own config for no gain, so an empty
        // resolution leaves --mcp-config off entirely, the same as the TTY route.
        _mcpConfigPath = ClaudeMcpConfig.Write(mcpServers ?? [], writeEmptyExplicit: unattended);

        // A hidden per-session system prompt (AC-180) the host folded into the options map — an embedded run's brief
        // (Autopilot's CEO), or the assistant's whole standing instruction. Applied at start through
        // --append-system-prompt-file, so it needs no post-start turn and no room on the command line.
        _systemPromptPath = ClaudePrivateTempFile.WriteSystemPrompt(
            _ResolveOption(options, WellKnownPluginSessionOptions.AppendSystemPrompt, defaultValue: null));
        var arguments = ClaudeSdkArguments.BuildArguments(permissionMode, effectiveModel, resumeSessionId, continueMostRecent: false, appendSystemPromptPath: _systemPromptPath, mcpConfigPath: _mcpConfigPath, strictMcpConfig: unattended);
        var environment = _BuildEnvironment(userHome);

        // AC-13: hand the agent its own session id as COCKPIT_PANE_ID, so it can name its own session to the
        // cockpit-session MCP server's set_status tool. The host sets this option per session; a session started
        // without it (an older host) simply gets no variable.
        if (_ResolveOption(options, WellKnownPluginSessionOptions.PaneId, defaultValue: null) is { Length: > 0 } paneId)
        {
            environment["COCKPIT_PANE_ID"] = paneId;
        }

        var subprocess = _subprocessFactory();
        _subprocess = subprocess;
        subprocess.Start(_executablePath, arguments, resolvedWorkingDirectory, environment);

        _stdoutPump = Task.Run(() => _PumpStdoutAsync(subprocess, _lifetime.Token), CancellationToken.None);
        // stderr must be drained or a full pipe deadlocks the child; its lines are diagnostic only.
        _stderrDrain = Task.Run(() => _DrainStderrAsync(subprocess, _lifetime.Token), CancellationToken.None);

        // Put an SDK client on the control channel so the CLI routes approvals here as can_use_tool requests. Sent
        // fire-and-forget (the reply is drained by the pump, not correlated), matching the host's control_request style.
        await subprocess.WriteLineAsync(ClaudeControlProtocol.BuildInitializeRequest(Guid.NewGuid().ToString()), cancellationToken).ConfigureAwait(false);

        // Apply the launch effort as the session's initial thinking-token budget; a live effort switch re-sends the
        // same control request. Left to the CLI's own default when the level is unknown.
        if (ClaudeOptionChoices.EffortThinkingTokens.TryGetValue(_effort, out var thinkingTokens))
        {
            await _SendControlRequestAsync(new { subtype = "set_max_thinking_tokens", max_thinking_tokens = thinkingTokens }, cancellationToken).ConfigureAwait(false);
        }

        _liveOptions = _BuildLiveOptions(effectiveModel, permissionMode);

        // AC-701: every session asks here, not just a resumed one — measured, a fresh session answers both
        // requests too (allowances are account-wide, and the context is non-zero from the system prompt alone),
        // so AC-660's "nothing worth asking for yet" was wrong. Same grace as the per-turn poll.
        try
        {
            await _PollUsageAsync().WaitAsync(_UsagePublishGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Slow, refused, or a session going away — a usage figure is a nicety, same tolerance the per-turn poll gives it.
        }
    }

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) =>
        _SendUserMessageAsync(text, images: null, cancellationToken);

    public Task SendUserMessageAsync(string text, IReadOnlyList<PluginImageAttachment>? images, CancellationToken cancellationToken) =>
        _SendUserMessageAsync(text, images, cancellationToken);

    private async Task _SendUserMessageAsync(string text, IReadOnlyList<PluginImageAttachment>? images, CancellationToken cancellationToken)
    {
        // Wire shape per the Agent SDK streaming docs: {"type":"user","message":{"role":"user","content":...}}. One
        // user-message object per stdin line keeps the same persistent multi-turn session alive. With attachments the
        // content becomes an array of blocks (text + one image block per attachment) — shape verified against
        // claude.exe 2.1.197; text-only keeps the plain-string content.
        object content = images is { Count: > 0 } ? _BuildContentBlocks(text, images) : text;

        var payload = new
        {
            type = "user",
            message = new { role = "user", content },
        };

        await _RequireSubprocess().WriteLineAsync(JsonSerializer.Serialize(payload), cancellationToken).ConfigureAwait(false);
    }

    private static object[] _BuildContentBlocks(string text, IReadOnlyList<PluginImageAttachment> images)
    {
        var blocks = new List<object> { new { type = "text", text } };
        foreach (var image in images)
        {
            blocks.Add(new
            {
                type = "image",
                source = new { type = "base64", media_type = image.MediaType, data = image.Base64Data },
            });
        }

        return [.. blocks];
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        await _SendControlRequestAsync(new { subtype = "interrupt" }, cancellationToken).ConfigureAwait(false);

        // AC-943: the CLI may already have retracted these via `control_cancel_request` (see `_HandleLine`), in
        // which case this snapshot is empty; denying whatever is left closes the gap for anything it did not.
        foreach (var toolUseId in _pendingApprovals.Keys.ToList())
        {
            if (_pendingApprovals.TryRemove(toolUseId, out var pending))
            {
                var line = ClaudeControlProtocol.BuildDecisionResponse(pending.RequestId, allow: false, pending.InputJson, denyMessage: "Cancelled — the turn was interrupted.");
                await _RequireSubprocess().WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // AC-664: the CLI summarises the conversation and carries on under the same session id. It rides the user-message
    // line because the control protocol has no compaction subtype (claude.exe 2.1.226) — the CLI parses `/compact`
    // out of the stream-json input itself; measured against a live spawn in this mode, see the ticket.
    public Task CompactContextAsync(CancellationToken cancellationToken = default) =>
        _SendUserMessageAsync("/compact", images: null, cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, CancellationToken cancellationToken = default) =>
        RespondToPermissionAsync(toolUseId, allow, answersJson: null, cancellationToken);

    public Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, CancellationToken cancellationToken) =>
        RespondToPermissionAsync(toolUseId, allow, answersJson, denyReason: null, cancellationToken);

    public async Task RespondToPermissionAsync(string toolUseId, bool allow, string? answersJson, string? denyReason, CancellationToken cancellationToken)
    {
        if (!_pendingApprovals.TryRemove(toolUseId, out var pending))
        {
            // The CLI auto-allowed this tool (never prompted) or the request already resolved — the UI affordance was
            // optimistic. Nothing to feed back over the control channel.
            return;
        }

        // The original input rides back as updatedInput on an allow — dropping it would run the tool with no
        // arguments. AC-715: the operator's answers to an AskUserQuestion are merged into it there.
        // AC-971: the host's delegated gate passes its own reason, since no operator denied anything there.
        var line = ClaudeControlProtocol.BuildDecisionResponse(
            pending.RequestId,
            allow,
            pending.InputJson,
            denyMessage: denyReason is { Length: > 0 } reason ? reason : "Denied by the cockpit operator.",
            answersJson);
        await _RequireSubprocess().WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    public Task SetLiveOptionAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        // Model and permission mode are switchable mid-session over the control protocol — the exact set_model /
        // set_permission_mode subtypes the host's ClaudeCliSession already round-trips against the live CLI. A key this
        // driver did not declare is contract drift, not an operator mistake, so it is ignored.
        return key switch
        {
            ModelOptionKey => _SetModelAndRemember(value, cancellationToken),
            PermissionModeOptionKey => _SendControlRequestAsync(new { subtype = "set_permission_mode", mode = value }, cancellationToken),
            EffortOptionKey => _SetEffort(value, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    private Task _SetModelAndRemember(string value, CancellationToken cancellationToken)
    {
        _model = string.IsNullOrWhiteSpace(value) ? null : value;
        return _SendControlRequestAsync(new { subtype = "set_model", model = _model }, cancellationToken);
    }

    private Task _SetEffort(string value, CancellationToken cancellationToken)
    {
        // An unknown effort level is contract drift (the host only offers the declared ones) — ignore it rather than
        // send the CLI a budget it did not ask for.
        if (!ClaudeOptionChoices.EffortThinkingTokens.TryGetValue(value, out var thinkingTokens))
        {
            return Task.CompletedTask;
        }

        _effort = value;
        // Field is snake_case (max_thinking_tokens) verbatim from the Agent SDK's Query.set_max_thinking_tokens — the
        // CLI silently drops an unknown camelCase field, which would leave the budget unchanged (the effort-not-live bug).
        return _SendControlRequestAsync(new { subtype = "set_max_thinking_tokens", max_thinking_tokens = thinkingTokens }, cancellationToken);
    }

    private async Task _SendControlRequestAsync(object request, CancellationToken cancellationToken)
    {
        var line = ClaudeControlProtocol.BuildRequest(Guid.NewGuid().ToString(), request);
        await _RequireSubprocess().WriteLineAsync(line, cancellationToken).ConfigureAwait(false);
    }

    private async Task _PumpStdoutAsync(IClaudeSdkSubprocess subprocess, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in subprocess.ReadStdoutLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                _HandleLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        catch (Exception exception)
        {
            _events.Publish(new PluginSessionError { SessionId = _sessionId, Message = exception.Message });
        }
        finally
        {
            // AC-539: a result line's publish is parked behind the usage poll, and a CLI that exits right after
            // printing one (an unresolvable --resume) ends stdout while that task still waits — completing first
            // drops the turn. Never throws: _PollUsageThenPublishAsync swallows its own poll failure.
            await _pendingResultPublish.ConfigureAwait(false);
            _events.TryComplete();
        }
    }

    // The in-flight _PollUsageThenPublishAsync, so the pump can wait for it before completing the stream. Only ever
    // touched from the pump thread (_HandleLine runs on it), so plain assignment is enough.
    private Task _pendingResultPublish = Task.CompletedTask;

    private void _HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // A malformed stream-json line is dropped rather than killing the whole pump — the session stays alive for
            // the well-formed lines around it.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                _sessionId = sid.GetString();
            }

            // Fold usage in before anything is published (AC-530). The turn's result line is both what closes the turn
            // and what carries the context window size, and the host reads Status off the back of the resulting
            // TurnCompleted — observing first is what makes that read see this turn rather than the previous one.
            _usage.Observe(root);

            var type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                ? typeProp.GetString()
                : null;

            // The host reads Status once per turn, off the back of this line's TurnCompleted
            // (SessionViewModel._RefreshLimits — no timer, no second read), so the poll must finish before those
            // events go out or every figure lands a turn late. It cannot be awaited here: the replies come back
            // up this very pump. Hence a separate task that polls first and publishes after.
            if (string.Equals(type, "result", StringComparison.Ordinal))
            {
                _pendingResultPublish = _PollUsageThenPublishAsync(line);
                return;
            }

            // Control-protocol lines are the CLI's permission requests and the replies to our own control_requests —
            // routed here, never to the transcript parser.
            if (ClaudeControlProtocol.IsControlLine(type))
            {
                if (ClaudeControlProtocol.TryParsePermissionRequest(root, out var requestId, out var toolUseId, out var toolName, out var inputJson))
                {
                    _pendingApprovals[toolUseId] = (requestId, inputJson);
                    _events.Publish(new PluginPermissionRequested
                    {
                        SessionId = _sessionId,
                        ToolUseId = toolUseId,
                        ToolName = toolName,
                        InputJson = inputJson,
                    });
                }
                else if (ClaudeControlProtocol.TryParseResponse(root, out var responseId, out var payload))
                {
                    // A reply nobody waits for (the initialize handshake, a set_model ack) is dropped, as before.
                    _CompleteControlResponse(responseId, payload);
                }
                else if (responseId.Length > 0)
                {
                    // A refused parse is the CLI answering `subtype:"error"`, which still names the request:
                    // release the awaiter rather than let it wait out its timeout.
                    _CompleteControlResponse(responseId, null);
                }
                else if (string.Equals(type, ClaudeControlProtocol.ControlCancelType, StringComparison.Ordinal)
                    && root.TryGetProperty("request_id", out var cancelledId) && cancelledId.ValueKind == JsonValueKind.String)
                {
                    // AC-943: the CLI retracted a `can_use_tool` request itself (interrupt, aborted turn, tool
                    // timeout) — drop the matching pending entry so a later Allow/Deny click writes nothing.
                    var cancelledRequestId = cancelledId.GetString();
                    var stale = _pendingApprovals.FirstOrDefault(entry => entry.Value.RequestId == cancelledRequestId).Key;
                    if (stale is not null)
                    {
                        _pendingApprovals.TryRemove(stale, out _);
                    }
                }

                return;
            }

            foreach (var evt in ClaudeStreamJson.ParseLine(line))
            {
                _events.Publish(evt);
            }
        }
    }

    private async Task _DrainStderrAsync(IClaudeSdkSubprocess subprocess, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in subprocess.ReadStderrLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Discard — draining is what keeps a verbose stderr from filling the pipe and deadlocking the child.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose.
        }
        catch (Exception)
        {
            // stderr is diagnostic; a read failure here must not surface as a session error.
        }
    }

    // Env var that asks the CLI to forward a sub-agent's own text/thinking into the stream (AC-146) — the Task
    // tool's activity would otherwise be invisible beyond the parent tool-call row and its final result. An env
    // var rather than a CLI flag deliberately: an older CLI that does not know it simply never sets the
    // corresponding internal option and forwards nothing, exactly like today — an unrecognised environment
    // variable is silently ignored, where an unrecognised CLI flag would refuse to start the process at all.
    // That is the whole of the graceful degradation this needs; no version sniffing required.
    private const string ForwardSubagentTextEnvironmentVariable = "CLAUDE_CODE_FORWARD_SUBAGENT_TEXT";

    // How long a `get_usage` figure stands before the next turn boundary asks again. The allowances move in
    // minutes, and unlike the context breakdown this one can reach the account's own usage endpoint.
    private static readonly TimeSpan _AllowancePollInterval = TimeSpan.FromMinutes(1);

    // A reply that never comes must not keep its awaiter alive for the session's lifetime.
    private static readonly TimeSpan _UsageRequestTimeout = TimeSpan.FromSeconds(15);

    // How long the turn's events wait for the poll — past it they go out anyway and the answer lands next turn.
    // AC-761 F2: widened from 2s, since a cold get_context_usage alone measured ~1.6s.
    private static readonly TimeSpan _UsagePublishGrace = TimeSpan.FromSeconds(3);

    // Asks the CLI for the two figures the pill renders — `get_usage` (what `/usage` prints) and
    // `get_context_usage` (what `/context` prints) — then publishes the turn's events. Replaces AC-549's
    // `claude -p "/usage"` subprocess and its `.claude.json` cache, which on 2.1.226 became a 35.8s assistant
    // turn that refreshed nothing.
    private async Task _PollUsageThenPublishAsync(string line)
    {
        try
        {
            await _PollUsageAsync().WaitAsync(_UsagePublishGrace, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Slow, refused, or a session going away. The turn must not wait on it — see the grace above.
        }

        foreach (var evt in ClaudeStreamJson.ParseLine(line))
        {
            _events.Publish(evt);
        }
    }

    private async Task _PollUsageAsync()
    {
        try
        {
            var mayPollAllowances = _MayPollAllowances();

            // AC-761 F2: both requests in flight together rather than one after the other — sequential, the two
            // round-trips could together outrun _UsagePublishGrace on a cold connection.
            var usageTask = mayPollAllowances
                ? _RequestControlAsync(new { subtype = "get_usage" })
                : Task.FromResult<JsonElement?>(null);
            var contextTask = _RequestControlAsync(new { subtype = "get_context_usage" });

            await Task.WhenAll(usageTask, contextTask).ConfigureAwait(false);

            if (mayPollAllowances && usageTask.Result is { } usage)
            {
                _usage.ObserveAccountWindows(ClaudeUsageWindows.Read(usage));
                // Stamped on success only, so a failed request is retried next turn instead of costing the interval.
                Interlocked.Exchange(ref _lastAllowancePollTicks, DateTimeOffset.UtcNow.Ticks);
            }

            if (contextTask.Result is { } context)
            {
                _usage.ObserveContextUsage(context);
            }
        }
        catch (Exception)
        {
            // A usage figure is a nicety; it must never surface as a session error.
        }
    }

    // Racy by design: two overlapping turns both seeing "yes" costs one extra round-trip.
    private bool _MayPollAllowances() =>
        DateTimeOffset.UtcNow.Ticks - Interlocked.Read(ref _lastAllowancePollTicks) >= _AllowancePollInterval.Ticks;

    // Sends a control_request and waits for the reply the CLI correlates on `request_id`. Null when the CLI
    // answered with an error, the reply did not arrive in time, or the session is going away.
    private async Task<JsonElement?> _RequestControlAsync(object request)
    {
        var requestId = Guid.NewGuid().ToString();
        var awaiter = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingControlResponses[requestId] = awaiter;

        try
        {
            await _RequireSubprocess().WriteLineAsync(ClaudeControlProtocol.BuildRequest(requestId, request), _lifetime.Token).ConfigureAwait(false);
            return await awaiter.Task.WaitAsync(_UsageRequestTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _pendingControlResponses.TryRemove(requestId, out _);
        }
    }

    private void _CompleteControlResponse(string requestId, JsonElement? payload)
    {
        if (_pendingControlResponses.TryRemove(requestId, out var awaiter))
        {
            awaiter.TrySetResult(payload);
        }
    }

    private Dictionary<string, string?> _BuildEnvironment(string userHome)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // The profile's own variables (AC-22) go in first: the credential drop and the config-dir rule below
        // must keep the last word, so an operator variable can never reintroduce a credential or point the CLI
        // at another profile's config.
        foreach (var (key, value) in _profileEnvironment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        // Default on (Raymond, 2026-07-29): sub-agent activity is worth seeing by default, collapsed under its
        // parent Task row rather than behind an opt-in toggle nobody finds. Set only when the profile did not
        // already supply its own value, so an operator who wants it off can still say so.
        environment.TryAdd(ForwardSubagentTextEnvironmentVariable, "1");

        // Drop any ANTHROPIC_* credential, inherited or profile-supplied: one that reaches the CLI silently moves
        // the session off the operator's subscription and onto API-key billing (the same rule the host's spawn
        // applies). Null tells the subprocess seam to remove the variable from the child's environment.
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                     .Concat(environment.Keys)
                     .Where(name => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            environment[key] = null;
        }

        // A non-default profile dir is exported as CLAUDE_CONFIG_DIR; a default-dir profile clears any inherited value
        // so the CLI uses its native home-root config/login.
        if (ClaudeConfigPaths.ResolveSpawnOverride(_config.ConfigDir, userHome) is { } configDirOverride)
        {
            environment[ClaudeConfigPaths.EnvironmentVariable] = configDirOverride;
        }
        else
        {
            environment[ClaudeConfigPaths.EnvironmentVariable] = null;
        }

        return environment;
    }

    private IReadOnlyList<PluginSessionLaunchOption> _BuildLiveOptions(string? model, string permissionMode)
    {
        // The current model rides the choices so the panel opens on what the session is actually using, even when it is
        // a pinned model or snapshot the suggestion list omits.
        var modelChoices = new List<string>(ClaudeOptionChoices.ModelSuggestions);
        if (model is { Length: > 0 } current && !modelChoices.Contains(current))
        {
            modelChoices.Insert(0, current);
        }

        var liveOptions = new List<PluginSessionLaunchOption>
        {
            new(ModelOptionKey, "Model", modelChoices, model) { ChoiceLabels = ClaudeOptionChoices.ModelLabels },
            new(EffortOptionKey, "Effort", ClaudeOptionChoices.EffortLevels, _effort) { ChoiceLabels = ClaudeOptionChoices.EffortLabels },
        };

        // A session launched in bypassPermissions shows no permission-mode switch: the CLI cannot leave bypass live, and
        // you do not casually step down from it mid-session (the host locks the same dropdown). The three switchable
        // modes open on the launched one.
        if (!string.Equals(permissionMode, "bypassPermissions", StringComparison.Ordinal))
        {
            liveOptions.Add(new PluginSessionLaunchOption(PermissionModeOptionKey, "Permission mode", ClaudeOptionChoices.LivePermissionModes, permissionMode)
                { ChoiceLabels = ClaudeOptionChoices.PermissionModeLabels });
        }

        return liveOptions;
    }

    private static string? _ResolveOption(IReadOnlyDictionary<string, string>? options, string key, string? defaultValue) =>
        options is not null && options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private IClaudeSdkSubprocess _RequireSubprocess() =>
        _subprocess ?? throw new InvalidOperationException($"{nameof(StartAsync)} must be called before sending to the session.");

    public async ValueTask DisposeAsync()
    {
        _events.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Before the subprocess teardown, not after it (AC-956). The CLI read both of these at startup and the
        // process is on its way out, so nothing still needs them — while the shutdown path hands this whole
        // teardown a bounded budget and hard-exits when it runs out. Deleting last meant deleting never, for any
        // session whose teardown ran long: measured five days of leftover mcp-configs, 28 of them holding a bearer
        // header. First is the one position that does not depend on how long the rest takes.
        ClaudePrivateTempFile.Delete(_mcpConfigPath);
        ClaudePrivateTempFile.Delete(_systemPromptPath);

        // Guarded so _lifetime.Dispose always runs even if the subprocess teardown throws something other than the
        // InvalidOperationException its own DisposeAsync catches (e.g. a Win32Exception out of Process.Kill) — otherwise
        // the CancellationTokenSource leaks and the pump tasks stay unobserved.
        try
        {
            if (_subprocess is not null)
            {
                await _subprocess.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var pump in new[] { _stdoutPump, _stderrDrain })
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
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
