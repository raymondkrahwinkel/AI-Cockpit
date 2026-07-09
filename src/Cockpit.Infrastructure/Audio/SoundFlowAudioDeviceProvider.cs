using SoundFlow.Abstracts;
using SoundFlow.Structs;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using CoreAudioDeviceInfo = Cockpit.Core.Audio.AudioDeviceInfo;

namespace Cockpit.Infrastructure.Audio;

/// <summary>
/// <see cref="IAudioDeviceProvider"/> over the shared SoundFlow <see cref="AudioEngine"/>: refreshes the
/// backend's device list on each call (so hot-plugged devices appear) and maps the native
/// <see cref="DeviceInfo"/> entries to the framework-free <see cref="CoreAudioDeviceInfo"/> the UI binds
/// to. Keeps SoundFlow types out of the Core/App layers.
/// </summary>
internal sealed class SoundFlowAudioDeviceProvider(AudioEngine engine) : IAudioDeviceProvider, ISingletonService
{
    public IReadOnlyList<CoreAudioDeviceInfo> GetInputDevices()
    {
        engine.UpdateAudioDevicesInfo();
        return Map(engine.CaptureDevices);
    }

    public IReadOnlyList<CoreAudioDeviceInfo> GetOutputDevices()
    {
        engine.UpdateAudioDevicesInfo();
        return Map(engine.PlaybackDevices);
    }

    private static List<CoreAudioDeviceInfo> Map(DeviceInfo[] devices)
    {
        var result = new List<CoreAudioDeviceInfo>(devices.Length);
        foreach (var device in devices)
        {
            result.Add(new CoreAudioDeviceInfo(device.Name, device.IsDefault));
        }

        return result;
    }
}
