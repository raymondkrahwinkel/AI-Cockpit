namespace Cockpit.Core.Voice;

/// <summary>
/// User-configurable voice-input settings, persisted under the <c>voice</c> section of
/// <c>cockpit.json</c> (same store pattern as notifications/transcript-display/layout). Voice is
/// opt-in: <see cref="IsEnabled"/> defaults to false so the Whisper runtime/model is never downloaded
/// or loaded for an operator who never turns it on.
/// </summary>
public sealed record VoiceSettings
{
    public bool IsEnabled { get; init; }

    /// <summary>Ggml model name (e.g. "large-v3-turbo", "base", "tiny") resolved to a Whisper.net <c>GgmlType</c> in Infrastructure.</summary>
    public string ModelName { get; init; } = "large-v3-turbo";

    /// <summary>
    /// When true, the transcription model follows the advisor's per-machine recommendation — the "Auto ★" item in
    /// the Options model dropdown (AC-68 slice 2). <see cref="ModelName"/> still holds the concrete model last
    /// resolved for it, so the speech-to-text service reads a real ggml name and needs no advisor. Defaults to true
    /// so a fresh install starts on the recommended model; an existing config saved before this key existed is read
    /// as an explicit choice (the entry coalesces a missing key to false), so a hand-picked model is never overridden.
    /// </summary>
    public bool ModelAutoSelected { get; init; } = true;

    public VoiceBackendPreference BackendPreference { get; init; } = VoiceBackendPreference.Auto;

    /// <summary>Whether the raw transcript is passed through the local Ollama cleanup step (punctuation/filler removal) before injection.</summary>
    public bool CleanupEnabled { get; init; } = true;

    /// <summary>
    /// When true, the shared voice-LLM step (STT cleanup + read-aloud naturalize/summarize) auto-detects the
    /// running local server (Ollama or LM Studio, via the same process detection as the memory breakdown) and
    /// reads its model list, rather than using <see cref="VoiceLlmBaseUrl"/>/<see cref="VoiceLlmModel"/>
    /// directly — those become the fallback when nothing is detected. On by default so a laptop on Ollama and a
    /// desktop on LM Studio both work without per-machine setup.
    /// </summary>
    public bool AutoDetectLocalLlm { get; init; } = true;

    /// <summary>Which detected server auto-detect prefers when both Ollama and LM Studio are running. Ignored when auto-detect is off.</summary>
    public LocalLlmPreference LocalLlmPreference { get; init; } = LocalLlmPreference.Auto;

    /// <summary>
    /// Model id the shared voice-LLM step (STT cleanup + read-aloud naturalize/summarize) asks the local server
    /// for. Preferred over an auto-picked model when auto-detect finds it on the server. Empty means "Auto" — no
    /// explicit choice, let the server's model list decide (the default). <c>gemma3:4b</c> reads Dutch best and
    /// <c>qwen2.5:3b</c> is a safe fallback if you pick one by hand.
    /// </summary>
    public string VoiceLlmModel { get; init; } = "";

    /// <summary>
    /// Base URL of the local OpenAI-compatible LLM server the shared voice-LLM step calls — Ollama
    /// (<c>http://localhost:11434</c>) or LM Studio (<c>http://localhost:1234</c>), and any other server that
    /// speaks the same API. Stored without the <c>/v1</c> suffix; the OpenAI SDK appends <c>/v1</c>. One
    /// endpoint serves both STT cleanup and read-aloud naturalize/summarize.
    /// </summary>
    public string VoiceLlmBaseUrl { get; init; } = "http://localhost:11434";

    /// <summary>Avalonia <c>Key</c> enum name for the push-to-talk hotkey, e.g. "F9".</summary>
    public string PushToTalkKeyName { get; init; } = "F9";

    /// <summary>
    /// When true, the push-to-talk hotkey also fires while the cockpit window has no focus (#34), via
    /// an OS-level registration (XDG GlobalShortcuts portal on Linux, a low-level keyboard hook on
    /// Windows) instead of the per-view KeyDown/KeyUp handlers. Off by default: opt-in like voice
    /// itself, so the portal/hook is never touched for an operator who never turns it on.
    /// </summary>
    public bool GlobalPushToTalk { get; init; }

    /// <summary>
    /// When true, a finished voice transcript is submitted immediately after injection instead of only
    /// being placed for review: the SDK session sends its input box, the TTY session writes a trailing
    /// carriage return into the pty. Off by default so the proofread-before-send behaviour stays the
    /// norm; opt-in for a hands-free dictate-and-go flow.
    /// </summary>
    public bool AutoSubmitAfterVoice { get; init; }

    /// <summary>
    /// SupertonicTTS speaker id (sid) for read-aloud (#35). One multilingual model voices every language, so
    /// this single speaker choice (the timbre) is used for both Dutch and English — mixed replies pass the
    /// language per segment rather than switching voice. The model downloads and caches on first use the same
    /// way the Whisper model does. Defaults to sid 1, the first offered voice.
    /// </summary>
    public int TtsVoiceSid { get; init; } = 1;

    /// <summary>
    /// Preferred base language for read-aloud, as an ISO-639-1 code ("en"/"nl"). Text with no language marker
    /// (verbatim, or a reply the naturalize/summarize pass left untagged) speaks in it, and that pass is told to
    /// lean to it — keeping code, names and genuinely foreign terms in their own language. Default "en".
    /// </summary>
    public string ReadAloudLanguage { get; init; } = "en";

    /// <summary>
    /// Whisper transcription language as an ISO-639-1 code ("nl", "en", …) or "auto" to let Whisper
    /// detect it. Defaults to "auto"; a fixed language is more reliable than detection when the operator
    /// always dictates in one language (auto-detect can mis-guess on short or accented utterances).
    /// </summary>
    public string SttLanguage { get; init; } = "auto";

    /// <summary>
    /// Name of the capture (microphone) device the voice pipeline records from. Empty = the system
    /// default device. Matched by name at capture start; a name that is no longer present falls back to
    /// the default. Stored by name because the native device handle is a per-run pointer.
    /// </summary>
    public string InputDeviceName { get; init; } = "";

    /// <summary>Name of the playback device read-aloud (#35) plays to. Empty = the system default device; same name-matching and fallback as <see cref="InputDeviceName"/>.</summary>
    public string OutputDeviceName { get; init; } = "";

    /// <summary>
    /// How read-aloud (#35) renders a reply before speaking it: <see cref="Cockpit.Core.Voice.ReadAloudMode.Verbatim"/>
    /// (no LLM pass), <see cref="Cockpit.Core.Voice.ReadAloudMode.Naturalized"/> (rewrite into natural speech) or
    /// <see cref="Cockpit.Core.Voice.ReadAloudMode.Summarized"/> (summarize to the essence). The last two reuse
    /// <see cref="VoiceLlmModel"/>/<see cref="VoiceLlmBaseUrl"/> and add a local LLM call per turn. A fresh install
    /// starts on Verbatim; a config saved before this key existed migrates from the old on/off naturalize flag.
    /// </summary>
    public ReadAloudMode ReadAloudMode { get; init; } = ReadAloudMode.Verbatim;

    /// <summary>
    /// When true, open-mic dictation listens continuously and detects speech start/stop itself (VAD
    /// endpointing) instead of requiring the push-to-talk hotkey to be held. Off by default: opt-in like
    /// voice itself, so the microphone is never held open for an operator who never turns it on.
    /// </summary>
    public bool OpenMicEnabled { get; init; }

    /// <summary>
    /// How long a trailing silence must last (milliseconds) before open-mic treats the utterance as
    /// finished and submits it — the endpointing pause. Tunable because the right value depends on the
    /// operator's speaking cadence; 800ms is a conversational default.
    /// </summary>
    public int OpenMicSilenceTimeoutMs { get; init; } = 800;

    /// <summary>
    /// When true, talking while the cockpit is reading aloud stops it (AC-9) — the microphone half of barge-in.
    /// A push-to-talk hold already interrupts playback and always has; this is the same thing without the key.
    /// <para>
    /// Needs <see cref="OpenMicEnabled"/>, and not as a policy: without open-mic there is no microphone held
    /// open, so there is nothing to hear you with.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Off by default, and not out of caution.</b> <see cref="StopReadAloudLevelThreshold"/> filters the
    /// room; it cannot filter the cockpit's own voice. On speakers the microphone hears the read-aloud — which
    /// is speech, and loud — so any threshold that lets your voice through lets the playback through too, and
    /// read-aloud would stop itself within a second of starting, every time, leaving an operator who never
    /// touched this setting to conclude that read-aloud is broken. The only real answer is echo cancellation,
    /// which this does not have. On a headset none of it applies, which is why the feature exists and why it
    /// asks first.
    /// </remarks>
    public bool StopReadAloudWhenSpeaking { get; init; }

    /// <summary>
    /// How loud (0..1 RMS, the same scale the waveform is drawn from) the microphone must get before
    /// <see cref="StopReadAloudWhenSpeaking"/> takes it for you rather than the room. Tunable for the reason the
    /// silence timeout is: a quiet room and a noisy one do not share a number, and neither do two microphones.
    /// </summary>
    public double StopReadAloudLevelThreshold { get; init; } = 0.15;
}
