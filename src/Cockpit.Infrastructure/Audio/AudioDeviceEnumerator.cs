using SoundFlow.Abstracts;
using SoundFlow.Structs;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Audio;
using CoreAudioDeviceInfo = Cockpit.Core.Audio.AudioDeviceInfo;

namespace Cockpit.Infrastructure.Audio;

/// <summary>
/// The single serialized gateway to the shared <see cref="AudioEngine"/>'s device enumeration. Capture,
/// playback and the Options device list all query the same engine instance, potentially from different
/// threads at once — a push-to-talk hold or TTS playback resolves its device on a background thread while
/// the Options dialog enumerates on the UI thread. The engine gives no concurrency guarantee, so every
/// <c>UpdateAudioDevicesInfo</c> + device-array read runs under one lock here rather than being duplicated
/// (and raced) across the three call sites.
/// </summary>
internal sealed class AudioDeviceEnumerator(AudioEngine engine) : ISingletonService
{
    private readonly object _gate = new();

    public IReadOnlyList<CoreAudioDeviceInfo> GetInputDevices()
    {
        lock (_gate)
        {
            engine.UpdateAudioDevicesInfo();
            return Map(engine.CaptureDevices);
        }
    }

    public IReadOnlyList<CoreAudioDeviceInfo> GetOutputDevices()
    {
        lock (_gate)
        {
            engine.UpdateAudioDevicesInfo();
            return Map(engine.PlaybackDevices);
        }
    }

    /// <summary>Empty name → the system default device (null); a configured name no longer present also falls back to the default, so an unplugged device never leaves capture/playback dead.</summary>
    public DeviceInfo? ResolveInputDevice(string preferredName)
    {
        lock (_gate)
        {
            engine.UpdateAudioDevicesInfo();
            return Resolve(preferredName, engine.CaptureDevices);
        }
    }

    public DeviceInfo? ResolveOutputDevice(string preferredName)
    {
        lock (_gate)
        {
            engine.UpdateAudioDevicesInfo();
            return Resolve(preferredName, engine.PlaybackDevices);
        }
    }

    private static DeviceInfo? Resolve(string preferredName, DeviceInfo[] devices)
    {
        var names = new string[devices.Length];
        for (var i = 0; i < devices.Length; i++)
        {
            names[i] = devices[i].Name;
        }

        var index = AudioDeviceResolver.FindIndex(preferredName, names);
        return index >= 0 ? devices[index] : null;
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
