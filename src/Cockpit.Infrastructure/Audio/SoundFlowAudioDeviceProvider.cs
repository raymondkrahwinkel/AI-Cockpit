using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;

namespace Cockpit.Infrastructure.Audio;

// `IAudioDeviceProvider` for the Options UI: a thin adapter over
// `AudioDeviceEnumerator`, mapping to framework-free `AudioDeviceInfo`.
// Keeps SoundFlow types out of the App layer.
internal sealed class SoundFlowAudioDeviceProvider(AudioDeviceEnumerator enumerator) : IAudioDeviceProvider
{
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => enumerator.GetInputDevices();

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => enumerator.GetOutputDevices();
}
