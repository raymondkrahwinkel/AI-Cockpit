namespace Zyra.Voice.Core.Configuration;

public sealed class AudioOptions
{
    /// <summary>
    /// Preferred input/output device name. Null falls back to the system default device.
    /// </summary>
    public string? PreferredDeviceName { get; set; }
}
