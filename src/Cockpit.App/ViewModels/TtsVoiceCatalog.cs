namespace Cockpit.App.ViewModels;

// SupertonicTTS voice-picker entries are speaker ids, not language models; language is selected per utterance.
// Measured 2026-08-02: 10 comparable speakers (sid 0–9); model swaps require re-measurement.
// Sid/style mapping is an inferred sorted upstream filename order; investigate it if a label sounds wrong.
public static class TtsVoiceCatalog
{
    // Index is the sid, by construction: the position in this array is the number handed to the synthesizer, so a
    // name and its sid cannot drift apart the way the old hand-written pair did ("Voice 2" was sid 3).
    private static readonly string[] _StyleNames =
        ["F1", "F2", "F3", "F4", "F5", "M1", "M2", "M3", "M4", "M5"];

    // Quoted from Supertone's voices page, shortened to what fits a picker. Kept beside the names so the two are
    // read — and corrected — together.
    private static readonly string[] _Descriptions =
    [
        "calm, slightly low",
        "bright and cheerful",
        "clear, announcer-style",
        "crisp and confident",
        "kind and gentle",
        "lively and upbeat",
        "deep and composed",
        "polished, authoritative",
        "soft, neutral-toned",
        "warm, soft-spoken",
    ];

    public static IReadOnlyList<TtsVoiceOption> Voices { get; } =
    [
        .. _StyleNames.Select((name, sid) =>
            new TtsVoiceOption($"{name} — {_Descriptions[sid]}", sid)),
    ];

    // sid 1 (F2) — unchanged from before this list grew, so nobody's saved choice moves under them.
    public static TtsVoiceOption Default { get; } = Voices.Single(voice => voice.Sid == 1);
}
