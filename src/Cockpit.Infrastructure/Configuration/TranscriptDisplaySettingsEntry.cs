using Cockpit.Core.TranscriptDisplay;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="TranscriptDisplaySettings"/> in the <c>transcriptDisplay</c> section of
/// <c>cockpit.json</c>.
/// </summary>
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
