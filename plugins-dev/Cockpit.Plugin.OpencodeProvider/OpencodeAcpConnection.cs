using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: a newline-delimited JSON-RPC 2.0 client over one persistent `opencode acp` process, copied from
// KimiAcpConnection (no shared JSON-RPC layer exists in Abstractions). Measured live to be wire-identical to
// Kimi's — id+method = server request, id only = reply, method only = notification, one read loop sorts them.
internal sealed class OpencodeAcpConnection : IAsyncDisposable
{
    private const string _JsonRpcVersion = "2.0";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Bounded with BoundedChannelFullMode.Wait — an unbounded channel behind a slow consumer is an OOM
    // vector; Wait applies backpressure instead of silently dropping a piece of transcript.
    private const int _ChannelCapacity = 1024;
    private static readonly BoundedChannelOptions _ChannelOptions = new(_ChannelCapacity) { FullMode = BoundedChannelFullMode.Wait };

    private readonly ICliSubprocess _subprocess;
    private readonly Channel<OpencodeNotification> _notifications = Channel.CreateBounded<OpencodeNotification>(_ChannelOptions);
    private readonly Channel<OpencodeServerRequest> _serverRequests = Channel.CreateBounded<OpencodeServerRequest>(_ChannelOptions);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCancellation = new();

    private long _nextId;
    private Task? _readLoop;
    private Task? _stderrDrain;

    public OpencodeAcpConnection(ICliSubprocess subprocess) => _subprocess = subprocess;

    // The `opencode acp` process id once spawned; `null` before start.
    public int? ProcessId => _subprocess.ProcessId;

    // Agent-to-client notifications (the streaming transcript), completing when the process exits.
    public IAsyncEnumerable<OpencodeNotification> Notifications => _notifications.Reader.ReadAllAsync();

    // Agent-initiated requests (approvals) that must be answered with `RespondAsync`.
    public IAsyncEnumerable<OpencodeServerRequest> ServerRequests => _serverRequests.Reader.ReadAllAsync();

    // Spawns `opencode acp` and starts pumping its stdout. Call once before any send. `environmentVariables`
    // carries `OPENCODE_CONFIG_CONTENT` (the forced permission policy — see OpencodeAcpSessionDriver) alongside
    // any auth/API-key variables the config supplies.
    public void Start(string executablePath, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        _subprocess.Start(executablePath, ["acp"], workingDirectory, environmentVariables);
        _readLoop = Task.Run(() => _ReadLoopAsync(_readCancellation.Token));

        // Drain stderr concurrently with stdout: opencode writes its own logs there (measured live), and an
        // unread stderr pipe would block the child mid-handshake — same D14-class risk Kimi guards against.
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

    // Sends a request and awaits its correlated reply's `result`; throws `OpencodeAcpException` on a JSON-RPC `error`.
    public async Task<JsonElement> SendRequestAsync(string method, object? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await _WriteMessageAsync(new { jsonrpc = _JsonRpcVersion, id, method, @params }, cancellationToken).ConfigureAwait(false);
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

    // Sends a notification (no reply expected), e.g. `session/cancel`.
    public Task SendNotificationAsync(string method, object? @params, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { jsonrpc = _JsonRpcVersion, method, @params }, cancellationToken);

    // Answers an agent-initiated request (a permission prompt), echoing its `id` back verbatim.
    public Task RespondAsync(JsonElement id, object? result, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { jsonrpc = _JsonRpcVersion, id, result }, cancellationToken);

    // Answers an unmodelled request kind with a structured JSON-RPC error rather than a made-up result the
    // agent could fail to deserialize.
    public Task RespondErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken = default) =>
        _WriteMessageAsync(new { jsonrpc = _JsonRpcVersion, id, error = new { code, message } }, cancellationToken);

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
                    await _DispatchAsync(line, cancellationToken).ConfigureAwait(false);
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
            // them rather than leave a caller awaiting a reply that can no longer come.
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new OpencodeAcpException("The opencode acp stream ended before this request was answered."));
            }
        }
    }

    // Async so a full bounded channel's WriteAsync can actually apply backpressure here — the read loop
    // awaits this per line, so a slow consumer stalls stdout reading (and eventually the child's own stdout
    // pipe) rather than a TryWrite silently dropping the message.
    private async Task _DispatchAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // opencode acp stdout is pure JSON-RPC; a non-JSON line is not a message we can act on.
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
                await _serverRequests.Writer.WriteAsync(new OpencodeServerRequest(idElement.Clone(), methodElement.GetString() ?? string.Empty, parameters), cancellationToken).ConfigureAwait(false);
            }
            else if (hasId)
            {
                _CompletePending(idElement, root);
            }
            else if (hasMethod)
            {
                await _notifications.Writer.WriteAsync(new OpencodeNotification(methodElement.GetString() ?? string.Empty, parameters), cancellationToken).ConfigureAwait(false);
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
            var message = _FormatJsonRpcError(error, out var code);
            completion.TrySetException(code.HasValue ? new OpencodeAcpException(message, code.Value) : new OpencodeAcpException(message));
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

    // Built from only error.code + error.message, never error.GetRawText() — error.data could echo request
    // params straight into a UI status line. Mirrors KimiAcpConnection's own precedent.
    private static string _FormatJsonRpcError(JsonElement error, out int? code)
    {
        code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeProperty) && codeProperty.ValueKind == JsonValueKind.Number
            ? codeProperty.GetInt32()
            : null;
        var codeText = code?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString() ?? "Unknown error"
            : "Unknown error";

        return $"opencode acp error {codeText}: {message}";
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
