using System.IO.Pipelines;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2288: a pty on a server as the terminal view takes one. Output is the server's stream, reattached from the last byte
// read after a dropped connection, so a reconnect continues the same pty; keystrokes and resizes go out one at a time, in order.
// A refusal (gone, revoked, out of scope) ends the stream, which the view reads as the process having exited.
internal sealed class RemotePty : IConPtyProcess
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly BackendApiClient _client;
    private readonly string _path;
    private readonly bool _closeOnDispose;
    private readonly Pipe _output = new();
    private readonly Channel<object> _outgoing = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();

    public RemotePty(BackendApiClient client, string id, bool closeOnDispose)
    {
        _client = client;
        _path = $"api/v1/terminals/{Uri.EscapeDataString(id)}";
        _closeOnDispose = closeOnDispose;
        OutputStream = _output.Reader.AsStream();
        InputStream = new _Input(_outgoing.Writer);
        _ = _ReadAsync();
        _ = _SendAsync();
    }

    public Stream InputStream { get; }

    public Stream OutputStream { get; }

    // No process on this machine: nothing here may measure or signal one.
    public int ProcessId => 0;

    public void Resize(short columns, short rows) => _outgoing.Writer.TryWrite(new _Size(columns, rows));

    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        _outgoing.Writer.TryComplete();
        if (_closeOnDispose)
        {
            _ = _client.DeleteAsync(_path).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task _ReadAsync()
    {
        long received = 0;
        var backoff = TimeSpan.FromMilliseconds(500);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var response = await _client.OpenStreamAsync($"{_path}/output?after={received}", _stop.Token).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
                    while (true)
                    {
                        var memory = _output.Writer.GetMemory(16 * 1024);
                        var read = await stream.ReadAsync(memory, _stop.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        received += read;
                        backoff = TimeSpan.FromMilliseconds(500);
                        _output.Writer.Advance(read);
                        if ((await _output.Writer.FlushAsync(_stop.Token).ConfigureAwait(false)).IsCompleted)
                        {
                            return;
                        }
                    }
                }
                catch (BackendApiException exception) when (exception.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or BackendApiException)
                {
                    await Task.Delay(backoff, _stop.Token).ConfigureAwait(false);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            await _output.Writer.CompleteAsync().ConfigureAwait(false);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task _SendAsync()
    {
        try
        {
            while (await _outgoing.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                while (_outgoing.Reader.TryRead(out var next))
                {
                    if (next is _Size size)
                    {
                        await _client.SendAsync<JsonElement>(HttpMethod.Post, $"{_path}/resize", new { columns = size.Columns, rows = size.Rows }, _stop.Token)
                            .ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                        continue;
                    }

                    // Keystrokes that queued while the last batch was on the line go out together.
                    using var batch = new MemoryStream();
                    batch.Write((byte[])next);
                    while (batch.Length < 32 * 1024 && _outgoing.Reader.TryPeek(out var more) && more is byte[] bytes && _outgoing.Reader.TryRead(out _))
                    {
                        batch.Write(bytes);
                    }

                    await _PostInputAsync(batch.GetBuffer().AsMemory(0, (int)batch.Length)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or BackendApiException)
        {
            // The server no longer takes this terminal's input: end the pane rather than type into nothing.
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    // A dropped connection is tried again, in order; a refusal is not.
    private async Task _PostInputAsync(ReadOnlyMemory<byte> bytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _client.PostBytesAsync($"{_path}/input", bytes, _stop.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private sealed record _Size(short Columns, short Rows);

    // Write-only and never blocking: the view writes keystrokes on the UI thread.
    private sealed class _Input(ChannelWriter<object> outgoing) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > 0 && !outgoing.TryWrite(buffer.ToArray()))
            {
                throw new IOException("The server terminal is closed.");
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
