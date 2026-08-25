namespace Cockpit.Core.Voice;

// AC-1013: A run of read-aloud sentences in one `Language` (ISO-639-1) — the unit the playback queue synthesizes; the single Supertonic voice can change pronunciation per segment without a timbre switch. Every batch today is one segment; the multi-segment shape survives from the marker-based mixed-language splitter removed with the naturalization LLM (AC-546).
public sealed record SpeechSegment(IReadOnlyList<string> Sentences, string Language);
