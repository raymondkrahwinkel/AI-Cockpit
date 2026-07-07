namespace Cockpit.Core.Voice;

/// <summary>
/// Safety thresholds for the Ollama transcript-cleanup step, ported 1:1 from WisperFlow's
/// <c>cleanup.py</c> (research: Cockpit-DotNet-Voice-Stack-2026-07-07.md §4) — cleanup is skipped
/// outright below <see cref="MinWordCount"/>, and its output is rejected as a suspected hallucination
/// once it runs longer than the raw transcript by more than <see cref="MaxLengthRatio"/> plus
/// <see cref="MaxLengthPadding"/> characters. Either case falls back to the raw transcript.
/// </summary>
public sealed record TranscriptCleanupOptions
{
    public double MaxLengthRatio { get; init; } = 1.3;

    public int MaxLengthPadding { get; init; } = 20;

    public int MinWordCount { get; init; } = 3;
}
