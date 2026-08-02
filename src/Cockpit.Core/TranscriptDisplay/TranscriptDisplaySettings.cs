namespace Cockpit.Core.TranscriptDisplay;

// User-configurable transcript-display settings, persisted under the `transcriptDisplay` section
// of `cockpit.json` (same store pattern as the profiles, notifications and session switching).
// Holds whether each transcript row shows the time it arrived (T7).
public sealed record TranscriptDisplaySettings
{
    // When true, every transcript row shows a small timestamp. Off by default to keep the transcript calm.
    public bool ShowTimestamps { get; init; }
}
