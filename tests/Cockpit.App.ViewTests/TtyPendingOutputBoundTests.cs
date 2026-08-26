using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-965: the pty reader must not turn a stalled UI thread into unbounded memory. Two field reports (macOS
/// 0.23.0.0, Linux 0.30.0.0) show the same signature — the UI thread stops, and from that moment the managed heap
/// climbs in a straight line until the machine dies.
/// <para>
/// The reader appends every read into <c>_outputPending</c> and the only consumer is a ~30 fps
/// <c>DispatcherTimer</c> on the UI thread. While that thread is away the buffer grows at the pty's full byte rate,
/// and reading on regardless is also what removes the backpressure a full pipe would otherwise have applied to the
/// child. No blocking is staged here: the test body owns the UI thread for its whole run, so the flush timer
/// genuinely never ticks — the standstill is the harness, not a simulation of one.
/// </para>
/// </summary>
[Collection("avalonia")]
public class TtyPendingOutputBoundTests
{
    // Far past the cap, so an unbounded reader is unmistakable in the assertion message rather than marginal.
    private const int FloodBytes = 64 * 1024 * 1024;

    [Fact]
    public void PendingPtyOutput_StopsGrowingWhileTheUiThreadNeverReachesItsFlushTimer()
    {
        long pending = 0;

        using var cancellation = new CancellationTokenSource();
        HeadlessAvalonia.Run(() =>
        {
            var view = new TtyView();
            var pty = new _FloodingPty(FloodBytes);

            // Off the UI thread, exactly as the real pump runs: `_ = PumpOutputAsync(...)` on a task, reading
            // while the flush timer is the only thing that empties what it collects.
            _ = Task.Run(() => view.PumpOutputAsync(pty, cancellation.Token), cancellation.Token);

            Assert.True(pty.Delivered.Wait(TimeSpan.FromSeconds(60)), "the flood never finished being read");
            pending = view.PendingPtyOutputBytes;
        });

        cancellation.Cancel();

        Assert.True(
            pending <= TtyView.MaxPendingPtyOutputBytes,
            $"the pty reader held {pending:N0} bytes of the {FloodBytes:N0} it read while the UI thread never "
            + $"drained it; the ceiling is {TtyView.MaxPendingPtyOutputBytes:N0}");
    }

    // A pty that hands out a fixed number of bytes as fast as it is asked for them, then goes quiet without ending
    // the stream — the reader stays in its loop, so what it is holding can be read while the pump is still live.
    private sealed class _FloodingPty(int totalBytes) : IConPtyProcess
    {
        private readonly _FloodStream _output = new(totalBytes);

        public Stream InputStream { get; } = Stream.Null;

        public Stream OutputStream => _output;

        public int ProcessId => 0;

        public ManualResetEventSlim Delivered => _output.Delivered;

        public void Resize(short columns, short rows)
        {
        }

        public void Dispose() => _output.Dispose();
    }

    private sealed class _FloodStream(int totalBytes) : Stream
    {
        private int _remaining = totalBytes;

        public ManualResetEventSlim Delivered { get; } = new(false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                Delivered.Set();
                // Quiet, not finished: returning 0 would end the pump and send it at the UI thread, which this
                // test is deliberately holding.
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            // Yield so the read completes off the caller's stack even when the data is already here — a
            // synchronously-completing read would run the whole pump loop inline on whoever started it.
            await Task.Yield();

            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Fill((byte)'x');
            _remaining -= count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Delivered.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
