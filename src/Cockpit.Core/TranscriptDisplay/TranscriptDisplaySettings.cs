namespace Cockpit.Core.TranscriptDisplay;

/// <summary>
/// User-configurable transcript-display settings, persisted under the <c>transcriptDisplay</c> section
/// of <c>cockpit.json</c> (same store pattern as the profiles, notifications and session switching).
/// Holds whether each transcript row shows the time it arrived (T7).
/// </summary>
public sealed record TranscriptDisplaySettings
{
    /// <summary>When true, every transcript row shows a small timestamp. Off by default to keep the transcript calm.</summary>
    public bool ShowTimestamps { get; init; }
}
