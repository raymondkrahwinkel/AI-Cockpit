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
    private readonly Dictionary<(Guid Tab, string Session), long> _retired = [];
    private readonly Dictionary<long, (BrowserIpcMessage Message, TaskCompletionSource<JsonElement> Answer)> _pending = [];
    private readonly Dictionary<long, (BrowserIpcMessage Message, CancellationTokenSource Cancel)> _incoming = [];
    private readonly Dictionary<BrowserIpcFrameBuffer, Guid> _buffers = [];
    private readonly List<Exception> _cleanupErrors = [];
    private readonly Channel<BrowserIpcMessage> _requests = Channel.CreateBounded<BrowserIpcMessage>(BrowserIpcLimits.QueuedRequests);
    private readonly Task _reader;
    private readonly Task _worker;
    private readonly byte[] _sendBuffer = new byte[BrowserIpcLimits.MessageBytes];
    private Task _cancellation = Task.CompletedTask;
    private long _nextRequest;
    private long _lastReceived;

    internal Task Completion => Task.WhenAll(_reader, _worker);
    internal IReadOnlyList<Exception> CleanupErrors => _cleanupErrors;

    internal BrowserIpcFrameBuffer OpenFrameBuffer(string trustedParent, BrowserIpcFrameDescriptor descriptor, Guid tab = default)
    {
        lock (_gate)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (_buffers.Values.Count(owner => owner == tab) >= 2 || _buffers.Count >= BrowserIpcLimits.Tabs * 2)
            {
                throw new InvalidOperationException("IPC frame buffer limit reached.");
            }
            var buffer = BrowserIpcFrameBuffer.Open(trustedParent, _generation, descriptor, _ReleaseBuffer);
            _buffers.Add(buffer, tab);
            return buffer;
        }
    }

    internal BrowserIpcFrameBuffer CreateFrameBuffer(string trustedParent, int width, int height, Guid tab = default)
    {
        lock (_gate)
        {
            _closed.Token.ThrowIfCancellationRequested();
            if (_buffers.Values.Count(owner => owner == tab) >= 2 || _buffers.Count >= BrowserIpcLimits.Tabs * 2)
            {
                throw new InvalidOperationException("IPC frame buffer limit reached.");
            }
            var buffer = BrowserIpcFrameBuffer.Create(trustedParent, _generation, width, height, _ReleaseBuffer, tab);
            _buffers.Add(buffer, tab);
            return buffer;
        }
    }

    private void _ReleaseBuffer(BrowserIpcFrameBuffer buffer)
    {
        lock (_gate)
        {
            _buffers.Remove(buffer);
        }
    }

    internal BrowserIpcConnection(Stream input, Stream output, Guid generation,
        Func<BrowserIpcMessage, CancellationToken, Task<JsonElement>> handle, (Guid Tab, string Session)? initialTab = null)
    {
        if (generation == Guid.Empty)
        {
            throw new ArgumentException("An IPC connection needs a fresh host generation.", nameof(generation));
        }
        _input = input;
        _output = output;
        _generation = generation;
        _handle = handle;
        if (initialTab is { } tab)
        {
            RegisterTab(tab.Tab, tab.Session);
        }
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
            if (_retired.ContainsKey((tab, session)))
            {
                throw new InvalidOperationException("A retired IPC tab id cannot be reused in this generation.");
            }
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
            if (_tabs.Remove((tab, session)))
            {
                foreach (var retired in _retired.Where(pair => pair.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToArray())
                {
                    _retired.Remove(retired.Key);
                }
                if (_retired.Count >= BrowserIpcLimits.Tabs)
                {
                    _retired.Remove(_retired.First().Key);
                }
                _retired[(tab, session)] = DateTimeOffset.UtcNow.Add(BrowserIpcLimits.Deadline).ToUnixTimeMilliseconds();
            }
            cancellations = _incoming.Values.Where(call => call.Message.TabId == tab && call.Message.SessionId == session)
                .Select(call => call.Cancel.CancelAsync()).ToArray();
            foreach (var call in _pending.Values.Where(call => call.Message.TabId == tab && call.Message.SessionId == session))
            {
                call.Answer.TrySetCanceled();
            }
        }
        try
        {
            await Task.WhenAll(cancellations).WaitAsync(BrowserIpcLimits.CancelGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _Close();
        }
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
            }
            int length = _Encode(message);
            cancel.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _CheckTab(tab, session);
                _pending.Add(message.RequestId, (message, answer));
            }
            try
            {
                await _WriteAsync(length, cancel.Token).ConfigureAwait(false);
            }
            catch
            {
                _Close();
                throw;
            }
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
                using var cancelSend = new CancellationTokenSource(BrowserIpcLimits.CancelSend);
                try
                {
                    await _SendAsync(message with { Kind = BrowserIpcKind.Cancel, Payload = JsonSerializer.SerializeToElement(false) }, cancelSend.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // A best-effort cancel must not delay or replace the original call's cancellation.
                }
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
                bool reject = false;
                lock (_gate)
                {
                    bool retired = _retired.TryGetValue((message.TabId, message.SessionId), out long retiredUntil) &&
                        retiredUntil > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (retired && message.Kind != BrowserIpcKind.Request)
                    {
                        continue;
                    }
                    if (retired == false)
                    {
                        _CheckTab(message.TabId, message.SessionId);
                    }
                    if (message.Kind == BrowserIpcKind.Request)
                    {
                        long remaining = message.Deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (message.RequestId <= _lastReceived || remaining > BrowserIpcLimits.Deadline.TotalMilliseconds)
                        {
                            throw new InvalidDataException("IPC request refused.");
                        }
                        _lastReceived = message.RequestId;
                        reject = retired || remaining <= 0 || _incoming.Count >= BrowserIpcLimits.PendingCalls;
                        if (reject == false)
                        {
                            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                            cancel.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                            _incoming.Add(message.RequestId, (message, cancel));
                            if (_requests.Writer.TryWrite(message) == false)
                            {
                                _incoming.Remove(message.RequestId);
                                cancel.Dispose();
                                reject = true;
                            }
                        }
                    }
                    else if (message.Kind is BrowserIpcKind.Response or BrowserIpcKind.Rejected)
                    {
                        if (_pending.TryGetValue(message.RequestId, out var pending))
                        {
                            _CheckCorrelation(pending.Message, message);
                            if (message.Kind == BrowserIpcKind.Response && message.Deadline > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
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
                await cancellation.WaitAsync(BrowserIpcLimits.CancelGrace).ConfigureAwait(false);
                if (reject)
                {
                    await _RejectAsync(message).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or ArgumentException or TimeoutException)
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
                Task<JsonElement>? handling = null;
                try
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    // Apply the grace bound even when a handler blocks before returning its task.
                    handling = Task.Run(() => _handle(message, cancel.Token), cancel.Token);
                    var result = await handling.WaitAsync(cancel.Token).ConfigureAwait(false);
                    cancel.Token.ThrowIfCancellationRequested();
                    await _SendAsync(message with { Kind = BrowserIpcKind.Response, Payload = result }, cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    if (handling is not null)
                    {
                        try
                        {
                            await handling.WaitAsync(BrowserIpcLimits.CancelGrace).ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            _Close();
                            return;
                        }
                        catch (Exception) when (handling.IsCompleted)
                        {
                            // Observe the cancelled handler before allowing the next call to run.
                        }
                    }
                    if (_closed.IsCancellationRequested == false)
                    {
                        await _RejectAsync(message).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException)
                {
                    await _RejectAsync(message).ConfigureAwait(false);
                }
                catch (Exception) when (handling?.IsFaulted == true)
                {
                    await _RejectAsync(message).ConfigureAwait(false);
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
            // A transport or protocol failure invalidates the connection; callers never receive a partial answer.
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

    private async Task _RejectAsync(BrowserIpcMessage message)
    {
        using var cancel = new CancellationTokenSource(BrowserIpcLimits.CancelSend);
        await _SendAsync(message with { Kind = BrowserIpcKind.Rejected, Payload = JsonSerializer.SerializeToElement(false) }, cancel.Token).ConfigureAwait(false);
    }

    private async Task _SendAsync(BrowserIpcMessage message, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        cancel.CancelAfter(BrowserIpcLimits.Deadline);
        await _writer.WaitAsync(cancel.Token).ConfigureAwait(false);
        try
        {
            int length = _Encode(message);
            cancel.Token.ThrowIfCancellationRequested();
            try
            {
                await _WriteAsync(length, cancel.Token).ConfigureAwait(false);
            }
            catch
            {
                _Close();
                throw;
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    private int _Encode(BrowserIpcMessage message)
    {
        using var stream = new MemoryStream(_sendBuffer, writable: true);
        try
        {
            JsonSerializer.Serialize(stream, message);
            return checked((int)stream.Position);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("IPC message is too large.", exception);
        }
    }

    private async Task _WriteAsync(int length, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await _output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await _output.WriteAsync(_sendBuffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void Close() => _Close();

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
            _retired.Clear();
            foreach (var call in _pending.Values)
            {
                call.Answer.TrySetCanceled(_closed.Token);
            }
            _pending.Clear();
            _requests.Writer.TryComplete();
            BrowserIpcCleanup.Try(_input.Dispose, _cleanupErrors);
            BrowserIpcCleanup.Try(_output.Dispose, _cleanupErrors);
            foreach (var buffer in _buffers.Keys.ToArray())
            {
                BrowserIpcCleanup.Try(buffer.Dispose, _cleanupErrors);
            }
            _buffers.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _Close();
        await Task.WhenAll(_reader, _worker).ConfigureAwait(false);
        try
        {
            await _cancellation.WaitAsync(BrowserIpcLimits.CancelGrace).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _cleanupErrors.Add(exception);
        }
        BrowserIpcCleanup.Try(_closed.Dispose, _cleanupErrors);
    }
}
