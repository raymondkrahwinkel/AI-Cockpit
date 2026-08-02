using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Cockpit.Plugin.KimiProvider;

// A newline-delimited JSON-RPC 2.0 client over one persistent `kimi acp` child process (AC-268) — the
// transport under `KimiAcpSessionDriver`, copied from
// `Cockpit.Plugin.CliAgentProvider.CodexAppServerConnection` (D10: no shared JSON-RPC layer exists in
// `Cockpit.Plugins.Abstractions`, so each provider keeps its own).
//
// Message classification: a line with both `id` and `method` is an agent-initiated request (e.g.
// `session/request_permission`) → `ServerRequests`; a line with only `id` is a reply to
// one of ours → resolves the pending call; a line with only `method` is a notification (almost always
// `session/update`) → `Notifications`. A single background read loop does this sorting so
// callers never race on the stream; stdin writes are serialized behind one lock so two calls can never
// interleave a message.
//
// Difference from the Codex app-server transport this was copied from: the Agent Client Protocol is strict
// JSON-RPC 2.0 (the protocol note's wire examples all carry `"jsonrpc":"2.0"`), where Codex's own
// app-server protocol omits that field — so every outgoing message here stamps it, unlike the Codex version.
internal sealed class KimiAcpConnection : IAsyncDisposable
{
    private const string _JsonRpcVersion = "2.0";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // P1-9b: bounded rather than unbounded, with BoundedChannelFullMode.Wait — kimi acp stdout is untrusted, and
    // an unbounded channel behind a slow/stalled consumer is an OOM vector on the host, not just this session.
    // Wait (not DropWrite) is the deliberate choice: a dropped session/update is a vanished piece of transcript,
    // silently. Backpressure — the read loop stops reading stdout until the consumer catches up, and the child
    // eventually blocks on its own stdout pipe — is the correct, visible failure mode here.
    private const int _ChannelCapacity = 1024;
    private static readonly BoundedChannelOptions _ChannelOptions = new(_ChannelCapacity) { FullMode = BoundedChannelFullMode.Wait };

    private readonly ICliSubprocess _subprocess;
    private readonly Channel<KimiNotification> _notifications = Channel.CreateBounded<KimiNotification>(_ChannelOptions);
    private readonly Channel<KimiServerRequest> _serverRequests = Channel.CreateBounded<KimiServerRequest>(_ChannelOptions);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _readCancellation = new();

    private long _nextId;
    private Task? _readLoop;
    private Task? _stderrDrain;

    public KimiAcpConnection(ICliSubprocess subprocess) => _subprocess = subprocess;

    // The `kimi acp` process id once spawned; `null` before start.
    public int? ProcessId => _subprocess.ProcessId;

    // Agent-to-client notifications (the streaming transcript), completing when the process exits.
    public IAsyncEnumerable<KimiNotification> Notifications => _notifications.Reader.ReadAllAsync();

    // Agent-initiated requests (approvals) that must be answered with `RespondAsync`.
    public IAsyncEnumerable<KimiServerRequest> ServerRequests => _serverRequests.Reader.ReadAllAsync();

    // Spawns `kimi acp` and starts pumping its stdout. Call once before any send.
    public void Start(string executablePath, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        _subprocess.Start(executablePath, ["acp"], workingDirectory, environmentVariables);
        _readLoop = Task.Run(() => _ReadLoopAsync(_readCancellation.Token));

        // Drain stderr to nothing, concurrently with stdout (D14): kimi acp writes its own logs there, and a
        // full, unread stderr pipe would block the child mid-handshake. We do not surface these lines — the
        // protocol lives on stdout — we just keep the pipe empty.
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

    // Sends a request and awaits its correlated reply's `result`; throws `KimiAcpException` on a JSON-RPC `error`.
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

    // Answers an agent-initiated request with a JSON-RPC error — the protocol-conform way to say "this client
    // cannot handle this request", used for request kinds the driver does not model. A structured error is a
    // valid response for any request regardless of its expected result shape, unlike a made-up result the
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
                completion.TrySetException(new KimiAcpException("The kimi acp stream ended before this request was answered."));
            }
        }
    }

    // P1-9b: async so a full bounded channel's WriteAsync can actually apply backpressure here — the read loop
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
            // kimi acp stdout is pure JSON-RPC; a non-JSON line is not a message we can act on.
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
                await _serverRequests.Writer.WriteAsync(new KimiServerRequest(idElement.Clone(), methodElement.GetString() ?? string.Empty, parameters), cancellationToken).ConfigureAwait(false);
            }
            else if (hasId)
            {
                _CompletePending(idElement, root);
            }
            else if (hasMethod)
            {
                await _notifications.Writer.WriteAsync(new KimiNotification(methodElement.GetString() ?? string.Empty, parameters), cancellationToken).ConfigureAwait(false);
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
            completion.TrySetException(code.HasValue ? new KimiAcpException(message, code.Value) : new KimiAcpException(message));
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

    // P1-8: builds the exception message from only error.code + error.message — never error.GetRawText(), which
    // would echo any error.data kimi attaches (it can carry the echoed request params) straight into the UI's
    // status line (SessionViewModel's Status = $"Failed to start: {ex.Message}"), leaking e.g. a Bearer token.
    // P1-10b: also hands the numeric code back separately (out parameter) so a caller can match on it, e.g.
    // KimiAcpSessionDriver.StartAsync recognising authRequired (-32000) rather than parsing it back out of text.
    private static string _FormatJsonRpcError(JsonElement error, out int? code)
    {
        code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeProperty) && codeProperty.ValueKind == JsonValueKind.Number
            ? codeProperty.GetInt32()
            : null;
        var codeText = code?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var messageProperty) && messageProperty.ValueKind == JsonValueKind.String
            ? messageProperty.GetString() ?? "Unknown error"
            : "Unknown error";

        return $"kimi acp error {codeText}: {message}";
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
