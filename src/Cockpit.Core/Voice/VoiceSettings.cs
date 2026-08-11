namespace Cockpit.Core.Voice;

// User-configurable voice-input settings, persisted under the `voice` section of
// `cockpit.json` (same store pattern as notifications/transcript-display/layout). Voice is
// opt-in: `IsEnabled` defaults to false so the Whisper runtime/model is never downloaded
// or loaded for an operator who never turns it on.
public sealed record VoiceSettings
{
    public bool IsEnabled { get; init; }

    // Ggml model name (e.g. "large-v3-turbo", "base", "tiny") resolved to a Whisper.net `GgmlType` in Infrastructure.
    public string ModelName { get; init; } = "large-v3-turbo";

    // When true, the transcription model follows the advisor's per-machine recommendation — the "Auto ★" item in
    // the Options model dropdown (AC-68 slice 2). `ModelName` still holds the concrete model last
    // resolved for it, so the speech-to-text service reads a real ggml name and needs no advisor. Defaults to true
    // so a fresh install starts on the recommended model; an existing config saved before this key existed is read
    // as an explicit choice (the entry coalesces a missing key to false), so a hand-picked model is never overridden.
    public bool ModelAutoSelected { get; init; } = true;

    public VoiceBackendPreference BackendPreference { get; init; } = VoiceBackendPreference.Auto;

    // Avalonia `Key` enum name for the push-to-talk hotkey, e.g. "F9".
    public string PushToTalkKeyName { get; init; } = "F9";

    // When true, the push-to-talk hotkey also fires while the cockpit window has no focus (#34), via
    // an OS-level registration (XDG GlobalShortcuts portal on Linux, a low-level keyboard hook on
    // Windows) instead of the per-view KeyDown/KeyUp handlers. Off by default: opt-in like voice
    // itself, so the portal/hook is never touched for an operator who never turns it on.
    public bool GlobalPushToTalk { get; init; }

    // When true, a finished voice transcript is submitted immediately after injection instead of only
    // being placed for review: the SDK session sends its input box, the TTY session writes a trailing
    // carriage return into the pty. Off by default so the proofread-before-send behaviour stays the
    // norm; opt-in for a hands-free dictate-and-go flow.
    public bool AutoSubmitAfterVoice { get; init; }

    // SupertonicTTS speaker id (sid) for read-aloud (#35). One multilingual model voices every language, so
    // this single speaker choice (the timbre) is used for both Dutch and English — mixed replies pass the
    // language per segment rather than switching voice. The model downloads and caches on first use the same
    // way the Whisper model does. Defaults to sid 1, the first offered voice.
    public int TtsVoiceSid { get; init; } = 1;

    // Read-aloud speaking rate passed to sherpa-onnx's generation config (AC-708). 1.0 is the model's natural
    // pace; sherpa-onnx itself defines the direction (Supertonic: >1 faster, <1 slower). Clamped to 0.5–2.0 at
    // the point of use, never persisted or sent to the native call outside that range.
    public double TtsSpeed { get; init; } = 1.0;

    // Preferred base language for read-aloud, as an ISO-639-1 code ("en"/"nl") — the language every enqueued
    // batch is synthesized in. One multilingual voice speaks it, so this is the language, not the timbre (that is
    // `TtsVoiceSid`). Default "en".
    public string ReadAloudLanguage { get; init; } = "en";

    // Whisper transcription language as an ISO-639-1 code ("nl", "en", …) or "auto" to let Whisper
    // detect it. Defaults to "auto"; a fixed language is more reliable than detection when the operator
    // always dictates in one language (auto-detect can mis-guess on short or accented utterances).
    public string SttLanguage { get; init; } = "auto";

    // Name of the capture (microphone) device the voice pipeline records from. Empty = the system
    // default device. Matched by name at capture start; a name that is no longer present falls back to
    // the default. Stored by name because the native device handle is a per-run pointer.
    public string InputDeviceName { get; init; } = "";

    // Name of the playback device read-aloud (#35) plays to. Empty = the system default device; same name-matching and fallback as `InputDeviceName`.
    public string OutputDeviceName { get; init; } = "";

    // When true, open-mic dictation listens continuously and detects speech start/stop itself (VAD
    // endpointing) instead of requiring the push-to-talk hotkey to be held. Off by default: opt-in like
    // voice itself, so the microphone is never held open for an operator who never turns it on.
    public bool OpenMicEnabled { get; init; }

    // How long a trailing silence must last (milliseconds) before open-mic treats the utterance as
    // finished and submits it — the endpointing pause. Tunable because the right value depends on the
    // operator's speaking cadence; 800ms is a conversational default.
    public int OpenMicSilenceTimeoutMs { get; init; } = 800;

    // When true, talking while the cockpit is reading aloud stops it (AC-9) — the microphone half of barge-in.
    // A push-to-talk hold already interrupts playback and always has; this is the same thing without the key.
    //
    // Needs `OpenMicEnabled`, and not as a policy: without open-mic there is no microphone held
    // open, so there is nothing to hear you with.
    // *Off by default, and not out of caution.* `StopReadAloudLevelThreshold` filters the
    // room; it cannot filter the cockpit's own voice. On speakers the microphone hears the read-aloud — which
    // is speech, and loud — so any threshold that lets your voice through lets the playback through too, and
    // read-aloud would stop itself within a second of starting, every time, leaving an operator who never
    // touched this setting to conclude that read-aloud is broken. The only real answer is echo cancellation,
    // which this does not have. On a headset none of it applies, which is why the feature exists and why it
    // asks first.
    public bool StopReadAloudWhenSpeaking { get; init; }

    // How loud (0..1 RMS, the same scale the waveform is drawn from) the microphone must get before
    // `StopReadAloudWhenSpeaking` takes it for you rather than the room. Tunable for the reason the
    // silence timeout is: a quiet room and a noisy one do not share a number, and neither do two microphones.
    public double StopReadAloudLevelThreshold { get; init; } = 0.15;
}
