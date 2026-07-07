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
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioFormat format, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in frames)
        {
            yield return frame;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
