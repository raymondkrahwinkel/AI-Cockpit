using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;

namespace Cockpit.Infrastructure.Audio;

// Registered only when the native backend could not be created. Empty device lists keep Options inert; actual
// capture/playback requests fail with the same diagnosis rather than accidentally looking like successful audio.
internal sealed class UnavailableAudioServices(string reason) : IAudioCaptureService, IAudioPlaybackService, IAudioDeviceProvider
{
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => [];

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => [];

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        if (cancellationToken.IsCancellationRequested)
        {
            yield break;
        }

        throw new InvalidOperationException(reason);
    }

    public Task PlayAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(reason));
}
