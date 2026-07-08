namespace Cockpit.App.ViewModels;

/// <summary>
/// Read-aloud voices offered by the Options flyout voice-picker (#35) — Piper/sherpa-onnx voice ids
/// verified against the real sherpa-onnx release assets (2026-07-08): each entry here has a matching
/// <c>vits-piper-{VoiceId}.tar.bz2</c> archive at
/// <c>github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/</c>.
/// </summary>
public static class PiperVoiceCatalog
{
    public static IReadOnlyList<PiperVoiceOption> Voices { get; } =
    [
        new("English (US) — Lessac", "en_US-lessac-medium"),
        new("English (US) — Amy", "en_US-amy-medium"),
        new("English (US) — Ryan", "en_US-ryan-medium"),
        new("English (US) — HFC Female", "en_US-hfc_female-medium"),
        new("English (GB) — Alan", "en_GB-alan-medium"),
        new("English (GB) — Jenny", "en_GB-jenny_dioco-medium"),
        new("English (GB) — Cori", "en_GB-cori-medium"),
        new("Dutch (NL) — Ronnie", "nl_NL-ronnie-medium"),
        new("Dutch (BE) — Nathalie", "nl_BE-nathalie-medium"),
        new("Dutch (BE) — Rdh", "nl_BE-rdh-medium"),
    ];

    public static PiperVoiceOption Default { get; } = Voices[0];
}
