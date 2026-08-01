namespace Cockpit.App.ViewModels;

/// <summary>
/// Voices offered by the Options → Voice → Assistant voice-picker. SupertonicTTS is one multilingual model whose
/// speakers are selected by sid, so a "voice" here is a speaker choice rather than a separate per-language model;
/// the same voice speaks both languages (language is passed per utterance, not by switching voice).
/// </summary>
/// <remarks>
/// <b>Measured, not assumed.</b> A throwaway probe (<c>OfflineTts.NumSpeakers</c>) against the downloaded model
/// reported <b>10 speakers (sid 0–9)</b> on <b>2026-08-02</b>, model
/// <c>sherpa-onnx-supertonic-3-tts-int8-2026-05-11</c> (org.k2fsa.sherpa.onnx 1.13.4). All ten synthesised a
/// Dutch+English test clip in roughly the same time (1.7–2.3 s for both sentences together on this machine), so
/// none was excluded as too slow. This count is a fact about that model build on that date — it is not re-checked
/// at build or run time, so a future model swap needs a re-measure and an update here, not an assumption that it
/// still holds.
/// <para>
/// <b>Label matches sid by construction.</b> The old two-voice list named "Voice 1" (sid 1) and "Voice 2" (sid 3) —
/// a name and a number that could drift apart. Every label here is generated from its own sid
/// (<c>$"Voice {sid}"</c>), so they cannot disagree.
/// </para>
/// </remarks>
public static class TtsVoiceCatalog
{
    /// <summary>Measured 2026-08-02 against sherpa-onnx-supertonic-3-tts-int8-2026-05-11 — see the type doc-comment.</summary>
    private const int MeasuredSpeakerCount = 10;

    public static IReadOnlyList<TtsVoiceOption> Voices { get; } =
        [.. Enumerable.Range(0, MeasuredSpeakerCount).Select(sid => new TtsVoiceOption($"Voice {sid}", sid))];

    /// <summary>sid 1 — unchanged from before this list grew, so nobody's saved choice moves under them.</summary>
    public static TtsVoiceOption Default { get; } = Voices.Single(voice => voice.Sid == 1);
}
