using Cockpit.Core.TranscriptDisplay;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `TranscriptDisplaySettings` in the `transcriptDisplay` section of
// `cockpit.json`.
internal sealed class TranscriptDisplaySettingsEntry
{
    public bool ShowTimestamps { get; set; }

    public static TranscriptDisplaySettingsEntry FromDomain(TranscriptDisplaySettings settings) => new()
    {
        ShowTimestamps = settings.ShowTimestamps,
    };

    public TranscriptDisplaySettings ToDomain() => new()
    {
        ShowTimestamps = ShowTimestamps,
    };
}
