using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// A newline-delimited JSON-RPC 2.0 client over one persistent <c>codex app-server</c> child process (Fase 3) —
/// the transport under <see cref="CodexAppServerSessionDriver"/>, the plugin-local analogue of the host's own
/// stream-json parsing for Claude. Unlike the proces-per-turn <see cref="CliSubprocessPluginSessionDriver"/>,
/// the process here lives for the whole session and speaks a bidirectional protocol: the client sends requests
/// and gets correlated replies, and the server sends its own requests (approvals) that the client must answer.
/// </summary>
/// <remarks>
/// Message classification (app-server omits the <c>"jsonrpc"</c> field on the wire, same as MCP): a line with
/// both <c>id</c> and <c>method</c> is a server-initiated request → <see cref="ServerRequests"/>; a line with
/// only <c>id</c> is a reply to one of ours → resolves the pending call; a line with only <c>method</c> is a
/// notification → <see cref="Notifications"/>. A single background read loop does this sorting so callers never
/// race on the stream; stdin writes are serialized behind one lock so two turns can never interleave a message.
/// </remarks>
internal sealed class CodexAppServerConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ICliSubprocess _subprocess;
    private readonly Channel<CodexNotification> _notifications = Channel.CreateUnbounded<CodexNotification>();
    private readonly Channel<CodexServerRequest> _serverRequests = Channel.CreateUnbounded<CodexServerRequest>();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCancellation = new();

    private long _nextId;
    private Task? _readLoop;
    private Task? _stderrDrain;

    public CodexAppServerConnection(ICliSubprocess subprocess) => _subprocess = subprocess;

    /// <summary>Server-to-client notifications (the streaming transcript), completing when the process exits.</summary>
    public IAsyncEnumerable<CodexNotification> Notifications => _notifications.Reader.ReadAllAsync();

    /// <summary>Server-initiated requests (approvals) that must be answered with <see cref="RespondAsync"/>.</summary>
    public IAsyncEnumerable<CodexServerRequest> ServerRequests => _serverRequests.Reader.ReadAllAsync();

    /// <summary>
    /// Spawns <c>codex app-server</c> and starts pumping its stdout. Call once before any send.
    /// <paramref name="configArgs"/> are <c>-c key=value</c> overrides (the session's MCP servers, #26) placed
    /// before the subcommand, where Codex expects global config flags.
    /// </summary>
    public void Start(string executablePath, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables, IReadOnlyList<string>? configArgs = null)
    {
        string[] arguments = configArgs is { Count: > 0 } ? [.. configArgs, "app-server"] : ["app-server"];
        _subprocess.Start(executablePath, arguments, workingDirectory, environmentVariables);
        _readLoop = Task.Run(() => _ReadLoopAsync(_readCancellation.Token));

        // Drain stderr to nothing, concurrently with stdout: codex app-server writes progress there, and a full,
        // unread stderr pipe would block the child mid-handshake (the deadlock ICliSubprocess warns about). We do
        // not surface these lines — the protocol lives on stdout — we just keep the pipe empty.
        _stderrDrain = Task.Run(() => _DrainStderrAsync(_readCancellation.Token));
    }

    private async Task _DrainStderrAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _subprocess.ReadStderrLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Discarded on purpose — see Start.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose — the read cancellation was tripped.
        }
    }

    /// <summary>Sends a request and awaits its correlated reply's <c>result</c>; throws <see cref="CodexAppServerException"/> on a JSON-RPC <c>error</c>.</summary>
    public async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await _WriteMessageAsync(new { id, method, @params }, cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                return await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification (no reply expected), e.g. the <c>initialized</c> handshake acknowledgement.</summary>
    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { method, @params }, cancellationToken);

    /// <summary>Answers a server-initiated request (an approval), echoing its <paramref name="id"/> back verbatim.</summary>
    public Task RespondAsync(JsonElement id, object? result, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { id, result }, cancellationToken);

    /// <summary>
    /// Answers a server-initiated request with a JSON-RPC error — the protocol-conform way to say "this client
    /// cannot handle this request", used for request kinds the driver does not model. A structured error is a
    /// valid response for any request regardless of its expected result shape, unlike a made-up result that the
    /// server could fail to deserialize.
    /// </summary>
    public Task RespondErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { id, error = new { code, message } }, cancellationToken);

    private async Task _WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _subprocess.WriteLineAsync(json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task _ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in _subprocess.ReadStdoutLinesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on dispose — the read cancellation was tripped.
        }
        finally
        {
            _notifications.Writer.TryComplete();
            _serverRequests.Writer.TryComplete();

            // The stream ended (process exited) — nothing will ever reply to an outstanding request, so fail
            // them rather than leave a turn awaiting a reply that can no longer come.
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new CodexAppServerException("The codex app-server stream ended before this request was answered."));
            }
        }
    }

    private void _Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // app-server stdout is pure JSON-RPC; a non-JSON line is not a message we can act on.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var hasId = root.TryGetProperty("id", out var idElement);
            var hasMethod = root.TryGetProperty("method", out var methodElement);
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : default;

            if (hasId && hasMethod)
            {
                _serverRequests.Writer.TryWrite(new CodexServerRequest(idElement.Clone(), methodElement.GetString() ?? string.Empty, parameters));
            }
            else if (hasId)
            {
                _CompletePending(idElement, root);
            }
            else if (hasMethod)
            {
                _notifications.Writer.TryWrite(new CodexNotification(methodElement.GetString() ?? string.Empty, parameters));
            }
        }
    }

    private void _CompletePending(JsonElement idElement, JsonElement root)
    {
        // Our own request ids are always numbers; a reply whose id we do not recognise is not ours to complete.
        if (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id) || !_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (root.TryGetProperty("error", out var error))
        {
            completion.TrySetException(new CodexAppServerException(error.GetRawText()));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
        }
        else
        {
            completion.TrySetResult(default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _readCancellation.CancelAsync().ConfigureAwait(false);

        foreach (var loop in new[] { _readLoop, _stderrDrain })
        {
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected — the loops observe the cancellation we just requested.
                }
            }
        }

        await _subprocess.DisposeAsync().ConfigureAwait(false);
        _readCancellation.Dispose();
        _writeLock.Dispose();
    }
}
