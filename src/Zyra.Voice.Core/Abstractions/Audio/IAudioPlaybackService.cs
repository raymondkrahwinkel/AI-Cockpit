using Zyra.Voice.Core.Audio;

namespace Zyra.Voice.Core.Abstractions.Audio;

/// <summary>
/// Plays back a raw PCM buffer on the default output device.
/// </summary>
public interface IAudioPlaybackService
{
    /// <summary>
    /// Plays <paramref name="pcm"/> and completes once playback has finished.
    /// </summary>
    Task PlayAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, CancellationToken cancellationToken = default);
}
