namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the Options input/output device combo boxes. <see cref="DeviceName"/> is null for the
/// "System default" entry (persisted as an empty name); otherwise it is the device name matched back at
/// capture/playback start.
/// </summary>
public sealed record AudioDeviceOption(string Label, string? DeviceName);
