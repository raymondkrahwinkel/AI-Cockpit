using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// In-memory <see cref="IAudioCaptureService"/> test double: yields a fixed set of frames, then keeps
/// "capturing" (mirroring a live device that streams until asked to stop) until the caller's token is
/// cancelled — same cancellation contract as <c>SoundFlowAudioCaptureService</c>.
/// </summary>
internal sealed class FakeAudioCaptureService(params byte[][] frames) : IAudioCaptureService
{
    // Interlocked: these count concurrent starts, and a plain ++ can lose the second microphone it has to prove.
    private int _captureCount;
    private int _activeCaptures;

    /// <summary>How many times a capture was opened — one device per call, as the real service logs it.</summary>
    public int CaptureCount => Volatile.Read(ref _captureCount);

    /// <summary>How many captures are still running. Nonzero after a stop is a microphone left open (AC-628).</summary>
    public int ActiveCaptures => Volatile.Read(ref _activeCaptures);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioFormat format, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _captureCount);
        Interlocked.Increment(ref _activeCaptures);
        try
        {
            foreach (var frame in frames)
            {
                yield return frame;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCaptures);
        }
    }
}
