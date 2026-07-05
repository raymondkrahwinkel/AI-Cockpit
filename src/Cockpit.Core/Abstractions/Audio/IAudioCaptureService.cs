using Cockpit.Core.Audio;

namespace Cockpit.Core.Abstractions.Audio;

/// <summary>
/// Captures raw PCM frames from the default input device.
/// </summary>
public interface IAudioCaptureService
{
    /// <summary>
    /// Streams captured PCM frames until <paramref name="cancellationToken"/> is triggered.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(AudioFormat format, CancellationToken cancellationToken = default);
}
