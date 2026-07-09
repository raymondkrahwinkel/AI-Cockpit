using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;

namespace Cockpit.Infrastructure.Audio;

/// <summary>
/// <see cref="IAudioDeviceProvider"/> for the Options UI: a thin adapter over
/// <see cref="AudioDeviceEnumerator"/>, which owns the (serialized) access to the shared SoundFlow engine
/// and the mapping to framework-free <see cref="AudioDeviceInfo"/>. Keeps SoundFlow types out of the App
/// layer.
/// </summary>
internal sealed class SoundFlowAudioDeviceProvider(AudioDeviceEnumerator enumerator) : IAudioDeviceProvider, ISingletonService
{
    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() => enumerator.GetInputDevices();

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => enumerator.GetOutputDevices();
}
