using Cockpit.Core.Audio;

namespace Cockpit.Core.Abstractions.Audio;

/// <summary>
/// Enumerates the machine's audio input (capture) and output (playback) devices for the Options UI.
/// Each call re-queries the backend so a device plugged in or removed after launch shows up.
/// </summary>
public interface IAudioDeviceProvider
{
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}
