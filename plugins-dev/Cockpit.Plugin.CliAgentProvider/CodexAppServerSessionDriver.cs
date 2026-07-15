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

    /// <summary>Option key for the per-session model override.</summary>
    public const string ModelOptionKey = "model";

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
    private string? _threadId;
    private string? _currentTurnId;
    private Task? _notificationPump;
    private Task? _serverRequestPump;

    public CodexAppServerSessionDriver(Func<ICliSubprocess> subprocessFactory, CliAgentConfig config, string executablePath)
    {
        _connection = new CodexAppServerConnection(subprocessFactory());
        _config = config;
        _executablePath = executablePath;
        _model = config.Model;
    }

    public PluginSessionCapabilities Capabilities { get; } = new(SupportsTools: true, SupportsPermissions: true);

    public string? SessionId => _threadId;

    public int? ProcessId => _connection.ProcessId;

    public IAsyncEnumerable<PluginSessionEvent> Events => _events.Reader.ReadAllAsync();

    public Task StartAsync(string? model = null, CancellationToken cancellationToken = default) =>
        StartAsync(model, workingDirectory: null, resumeSessionId: null, options: null, mcpServers: null, cancellationToken);

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

        // The session's MCP servers (#26) become -c config overrides on the app-server spawn; any bearer token
        // rides the process environment, never the command line (see CodexMcpConfig).
        var mcpLaunch = CodexMcpConfig.Build(mcpServers);
        var environmentVariables = _config.BuildEnvironmentVariables();
        foreach (var (key, value) in mcpLaunch.EnvironmentVariables)
        {
            environmentVariables[key] = value;
        }

        _connection.Start(_executablePath, _ResolveProcessWorkingDirectory(workingDirectory), environmentVariables, mcpLaunch.ConfigArgs);
        _notificationPump = Task.Run(() => _PumpNotificationsAsync(_lifetime.Token), CancellationToken.None);
        _serverRequestPump = Task.Run(() => _PumpServerRequestsAsync(_lifetime.Token), CancellationToken.None);

        await _connection.SendRequestAsync("initialize", new { clientInfo = new { name = _ClientName, version = _ClientVersion } }, cancellationToken).ConfigureAwait(false);
        await _connection.SendNotificationAsync("initialized", null, cancellationToken).ConfigureAwait(false);

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
        _events.Writer.TryWrite(new PluginSessionInitialized { SessionId = threadId, Tools = [] });
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
        var input = new object[] { new { type = "text", text } };
        _ = _SendTurnAsync(threadId, input, cancellationToken);
        return Task.CompletedTask;
    }

    private async Task _SendTurnAsync(string threadId, object[] input, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.SendRequestAsync("turn/start", new { threadId, input }, cancellationToken).ConfigureAwait(false);
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

            case "item/started":
                _HandleToolItem(notification.Params, completed: false);
                break;

            case "item/completed":
                _HandleToolItem(notification.Params, completed: true);
                break;

            case "turn/completed":
                _HandleTurnCompleted(notification.Params);
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
        });
        _currentTurnId = null;
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
