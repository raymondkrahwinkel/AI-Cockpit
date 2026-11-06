using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;

namespace Cockpit.Plugin.Browser.Ipc;

internal sealed class BrowserIpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Guid _generation;
    private readonly Func<BrowserIpcMessage, CancellationToken, Task<JsonElement>> _handle;
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _calls = new(BrowserIpcLimits.PendingCalls, BrowserIpcLimits.PendingCalls);
    private readonly object _gate = new();
    private readonly HashSet<(Guid Tab, string Session)> _tabs = [];
    private readonly Dictionary<long, (BrowserIpcMessage Message, TaskCompletionSource<JsonElement> Answer)> _pending = [];
    private readonly Dictionary<long, (BrowserIpcMessage Message, CancellationTokenSource Cancel)> _incoming = [];
    private readonly List<BrowserIpcFrameBuffer> _buffers = [];
    private readonly Channel<BrowserIpcMessage> _requests = Channel.CreateBounded<BrowserIpcMessage>(BrowserIpcLimits.QueuedRequests);
    private readonly Task _reader;
    private readonly Task _worker;
    private readonly byte[] _sendBuffer = new byte[BrowserIpcLimits.MessageBytes];
    private Task _cancellation = Task.CompletedTask;
    private long _nextRequest;
    private long _lastReceived;

    internal Task Completion => Task.WhenAll(_reader, _worker);

    internal BrowserIpcFrameBuffer OpenFrameBuffer(string trustedParent, BrowserIpcFrameDescriptor descriptor)
    {
        lock (_gate)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (_buffers.Count >= 2)
            {
                throw new InvalidOperationException("IPC frame buffer limit reached.");
            }
            var buffer = BrowserIpcFrameBuffer.Open(trustedParent, _generation, descriptor);
            _buffers.Add(buffer);
            return buffer;
        }
    }

    internal BrowserIpcFrameBuffer CreateFrameBuffer(string trustedParent, int width, int height)
    {
        lock (_gate)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (_buffers.Count >= 2)
            {
                throw new InvalidOperationException("IPC frame buffer limit reached.");
            }
            var buffer = BrowserIpcFrameBuffer.Create(trustedParent, _generation, width, height);
            _buffers.Add(buffer);
            return buffer;
        }
    }

    internal BrowserIpcConnection(Stream input, Stream output, Guid generation,
        Func<BrowserIpcMessage, CancellationToken, Task<JsonElement>> handle)
    {
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("An IPC connection needs a fresh host generation.", nameof(generation));
        }
        _input = input;
        _output = output;
        _generation = generation;
        _handle = handle;
        _worker = _WorkAsync();
        _reader = _ReadAsync();
    }

    internal void RegisterTab(Guid tab, string session)
    {
        if (tab == Guid.Empty || string.IsNullOrWhiteSpace(session) || session.Length > 128)
        {
            throw new ArgumentException("Invalid IPC tab or session.");
        }
        lock (_gate)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (_tabs.Count >= BrowserIpcLimits.Tabs || _tabs.Add((tab, session)) == false)
            {
                throw new InvalidOperationException("IPC tab registration refused.");
            }
        }
    }

    internal async Task UnregisterTabAsync(Guid tab, string session)
    {
        Task[] cancellations;
        lock (_gate)
        {
            _tabs.Remove((tab, session));
            cancellations = _incoming.Values.Where(call => call.Message.TabId == tab && call.Message.SessionId == session)
                .Select(call => call.Cancel.CancelAsync()).ToArray();
            foreach (var call in _pending.Values.Where(call => call.Message.TabId == tab && call.Message.SessionId == session))
            {
                call.Answer.TrySetCanceled();
            }
        }
        await Task.WhenAll(cancellations).ConfigureAwait(false);
    }

    internal async Task<JsonElement> CallAsync(Guid tab, string session, string operation, JsonElement payload,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_calls.Wait(0) == false)
        {
            throw new InvalidOperationException("Too many pending IPC calls.");
        }
        try
        {
            return await _CallAsync(tab, session, operation, payload, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _calls.Release();
        }
    }

    private async Task<JsonElement> _CallAsync(Guid tab, string session, string operation, JsonElement payload,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > BrowserIpcLimits.Deadline || string.IsNullOrWhiteSpace(operation) || operation.Length > 128)
        {
            throw new ArgumentException("Invalid IPC operation or deadline.");
        }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        cancel.CancelAfter(timeout);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        BrowserIpcMessage message;
        // Allocate request ids under the write lock so the peer can reject replay without an unbounded id cache.
        await _writer.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _CheckTab(tab, session);
                if (_pending.Count >= BrowserIpcLimits.PendingCalls)
                {
                    throw new InvalidOperationException("Too many pending IPC calls.");
                }
                message = new(BrowserIpcLimits.Version, _generation, tab, session, checked(++_nextRequest),
                    BrowserIpcKind.Request, DateTimeOffset.UtcNow.Add(timeout).ToUnixTimeMilliseconds(), operation, payload);
                _pending.Add(message.RequestId, (message, answer));
            }
            await _WriteAsync(message, cancel.Token).ConfigureAwait(false);
        }
        catch
        {
            _Close();
            throw;
        }
        finally
        {
            _writer.Release();
        }
        try
        {
            return await answer.Task.WaitAsync(cancel.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(message.RequestId);
            }
            if (answer.Task.IsCompletedSuccessfully == false && _closed.IsCancellationRequested == false)
            {
                await _SendAsync(message with { Kind = BrowserIpcKind.Cancel, Payload = JsonSerializer.SerializeToElement(false) }, _closed.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task _ReadAsync()
    {
        var prefix = new byte[4];
        var buffer = new byte[BrowserIpcLimits.MessageBytes];
        try
        {
            while (_closed.IsCancellationRequested == false)
            {
                await _input.ReadExactlyAsync(prefix.AsMemory(0, 1), _closed.Token).ConfigureAwait(false);
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                frameTimeout.CancelAfter(BrowserIpcLimits.Deadline);
                await _input.ReadExactlyAsync(prefix.AsMemory(1), frameTimeout.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                if (length <= 0 || length > buffer.Length)
                {
                    throw new InvalidDataException("Invalid IPC message length.");
                }
                await _input.ReadExactlyAsync(buffer.AsMemory(0, length), frameTimeout.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize<BrowserIpcMessage>(buffer.AsSpan(0, length));
                if (message is null || message.Version != BrowserIpcLimits.Version || message.Generation != _generation ||
                    message.RequestId <= 0 || Enum.IsDefined(message.Kind) == false ||
                    string.IsNullOrWhiteSpace(message.Operation) || message.Operation.Length > 128 ||
                    message.Payload.ValueKind == JsonValueKind.Undefined)
                {
                    throw new InvalidDataException("Invalid IPC envelope.");
                }
                Task cancellation = Task.CompletedTask;
                lock (_gate)
                {
                    _CheckTab(message.TabId, message.SessionId);
                    if (message.Kind == BrowserIpcKind.Request)
                    {
                        long remaining = message.Deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (message.RequestId <= _lastReceived || remaining <= 0 || remaining > BrowserIpcLimits.Deadline.TotalMilliseconds ||
                            _incoming.Count >= BrowserIpcLimits.QueuedRequests)
                        {
                            throw new InvalidDataException("IPC request refused.");
                        }
                        _lastReceived = message.RequestId;
                        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                        cancel.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                        _incoming.Add(message.RequestId, (message, cancel));
                        if (_requests.Writer.TryWrite(message) == false)
                        {
                            throw new InvalidDataException("IPC request queue is full.");
                        }
                    }
                    else if (message.Kind == BrowserIpcKind.Response)
                    {
                        if (_pending.TryGetValue(message.RequestId, out var pending))
                        {
                            _CheckCorrelation(pending.Message, message);
                            if (message.Deadline > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                            {
                                pending.Answer.TrySetResult(message.Payload);
                            }
                            else
                            {
                                pending.Answer.TrySetCanceled();
                            }
                        }
                        else if (message.RequestId > _nextRequest)
                        {
                            throw new InvalidDataException("Unknown IPC response.");
                        }
                    }
                    else if (_incoming.TryGetValue(message.RequestId, out var incoming))
                    {
                        _CheckCorrelation(incoming.Message, message);
                        cancellation = incoming.Cancel.CancelAsync();
                    }
                }
                await cancellation.ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or ArgumentException)
        {
            _Close();
        }
        finally
        {
            _Close();
        }
    }

    private async Task _WorkAsync()
    {
        try
        {
            await foreach (var message in _requests.Reader.ReadAllAsync(_closed.Token).ConfigureAwait(false))
            {
                CancellationTokenSource cancel;
                lock (_gate)
                {
                    cancel = _incoming[message.RequestId].Cancel;
                }
                try
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    var result = await _handle(message, cancel.Token).WaitAsync(cancel.Token).ConfigureAwait(false);
                    cancel.Token.ThrowIfCancellationRequested();
                    await _SendAsync(message with { Kind = BrowserIpcKind.Response, Payload = result }, cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    // Stop this generation rather than accumulate handlers that ignore cancellation.
                    _Close();
                    return;
                }
                finally
                {
                    lock (_gate)
                    {
                        _incoming.Remove(message.RequestId);
                    }
                    cancel.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // A failed handler or write invalidates the connection; callers see cancellation, never a partial answer.
            _Close();
        }
        finally
        {
            lock (_gate)
            {
                foreach (var call in _incoming.Values)
                {
                    call.Cancel.Dispose();
                }
                _incoming.Clear();
            }
        }
    }

    private void _CheckTab(Guid tab, string session)
    {
        _closed.Token.ThrowIfCancellationRequested();
        if (_tabs.Contains((tab, session)) == false)
        {
            throw new InvalidDataException("Unknown IPC tab or session.");
        }
    }

    private static void _CheckCorrelation(BrowserIpcMessage expected, BrowserIpcMessage actual)
    {
        if (expected.TabId != actual.TabId || expected.SessionId != actual.SessionId ||
            expected.Operation != actual.Operation || expected.Deadline != actual.Deadline)
        {
            throw new InvalidDataException("IPC response binding mismatch.");
        }
    }

    private async Task _SendAsync(BrowserIpcMessage message, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        cancel.CancelAfter(BrowserIpcLimits.Deadline);
        await _writer.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            await _WriteAsync(message, cancel.Token).ConfigureAwait(false);
        }
        catch
        {
            _Close();
            throw;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task _WriteAsync(BrowserIpcMessage message, CancellationToken cancellationToken)
    {
        var bytes = _sendBuffer;
        using var stream = new MemoryStream(bytes, writable: true);
        JsonSerializer.Serialize(stream, message);
        int length = checked((int)stream.Position);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await _output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(bytes.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void _Close()
    {
        lock (_gate)
        {
            if (_closed.IsCancellationRequested)
            {
                return;
            }
            _cancellation = _closed.CancelAsync();
            _tabs.Clear();
            foreach (var call in _pending.Values)
            {
                call.Answer.TrySetCanceled(_closed.Token);
            }
            _pending.Clear();
            _requests.Writer.TryComplete();
            _input.Dispose();
            _output.Dispose();
            foreach (var buffer in _buffers)
            {
                buffer.Dispose();
            }
            _buffers.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _Close();
        await Task.WhenAll(_reader, _worker).ConfigureAwait(false);
        await _cancellation.ConfigureAwait(false);
        _closed.Dispose();
    }
}
