namespace Cockpit.Core.Configuration;

public sealed class AudioOptions
{
    // Preferred input/output device name. Null falls back to the system default device.
    public string? PreferredDeviceName { get; set; }
}
