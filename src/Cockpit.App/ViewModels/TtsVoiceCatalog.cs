namespace Cockpit.App.ViewModels;

/// <summary>
/// Read-aloud voices offered by the Options flyout voice-picker (#35). SupertonicTTS is one multilingual
/// model whose speakers are selected by sid, so a "voice" here is a speaker choice rather than a separate
/// per-language model. The two offered — sid 1 and sid 3 — are the pair picked by ear as the clearest for
/// Dutch+English; the same voice speaks both languages (language is passed per utterance, not by switching
/// voice).
/// </summary>
public static class TtsVoiceCatalog
{
    public static IReadOnlyList<TtsVoiceOption> Voices { get; } =
    [
        new("Voice 1", 1),
        new("Voice 2", 3),
    ];

    public static TtsVoiceOption Default { get; } = Voices[0];
}
