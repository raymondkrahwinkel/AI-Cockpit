namespace Cockpit.Core.Audio;

/// <summary>
/// A selectable audio input or output device, surfaced to the Options UI so the operator can pick which
/// microphone the voice pipeline captures from and which output read-aloud plays to. Identified by
/// <see cref="Name"/> — the native device handle is a per-run pointer, so the name is what gets persisted
/// and matched back on the next launch.
/// </summary>
public sealed record AudioDeviceInfo(string Name, bool IsSystemDefault);
