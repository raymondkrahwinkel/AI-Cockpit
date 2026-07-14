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

    public VoiceBackendPreference BackendPreference { get; init; } = VoiceBackendPreference.Auto;

    /// <summary>Whether the raw transcript is passed through the local Ollama cleanup step (punctuation/filler removal) before injection.</summary>
    public bool CleanupEnabled { get; init; } = true;

    /// <summary>
    /// When true, the cleanup/naturalize step auto-detects the running local server (Ollama or LM Studio, via the
    /// same process detection as the memory breakdown) and reads its model list, rather than using
    /// <see cref="CleanupBaseUrl"/>/<see cref="CleanupModel"/> directly — those become the fallback when nothing is
    /// detected. On by default so a laptop on Ollama and a desktop on LM Studio both work without per-machine setup.
    /// </summary>
    public bool AutoDetectLocalLlm { get; init; } = true;

    /// <summary>Which detected server auto-detect prefers when both Ollama and LM Studio are running. Ignored when auto-detect is off.</summary>
    public LocalLlmPreference LocalLlmPreference { get; init; } = LocalLlmPreference.Auto;

    /// <summary>Model id the cleanup/naturalize step asks the local server for (e.g. "qwen2.5:3b-instruct" on Ollama, or an LM Studio model id). Preferred over an auto-picked model when auto-detect finds it on the server.</summary>
    public string CleanupModel { get; init; } = "qwen2.5:3b-instruct";

    /// <summary>
    /// Base URL of the local OpenAI-compatible LLM server the cleanup/naturalize step calls — Ollama
    /// (<c>http://localhost:11434</c>) or LM Studio (<c>http://localhost:1234</c>), and any other server that
    /// speaks the same API. Stored without the <c>/v1</c> suffix; the service appends
    /// <c>/v1/chat/completions</c>, the endpoint both backends serve.
    /// </summary>
    public string CleanupBaseUrl { get; init; } = "http://localhost:11434";

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
    /// Piper voice id for read-aloud (#35), e.g. "en_US-lessac-medium" or "nl_NL-ronnie-medium" — also
    /// the sherpa-onnx model archive name (<c>vits-piper-{id}.tar.bz2</c>), downloaded and cached on
    /// first use the same way the Whisper model is. This is the English/primary voice; mixed Dutch/English
    /// read-aloud routes Dutch segments to <see cref="TtsVoiceIdDutch"/> instead.
    /// </summary>
    public string TtsVoiceId { get; init; } = "en_US-lessac-medium";

    /// <summary>
    /// Piper voice id for the Dutch segments of a read-aloud reply. No single sherpa-onnx voice covers both
    /// Dutch and English, so when read-aloud naturalization tags language runs (<c>[[nl]]</c>/<c>[[en]]</c>),
    /// the Dutch runs are synthesized with this voice and the English runs with <see cref="TtsVoiceId"/>.
    /// </summary>
    public string TtsVoiceIdDutch { get; init; } = "nl_NL-ronnie-medium";

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
    /// When true, read-aloud (#35) first rewrites the assistant text into natural spoken sentences via the
    /// local LLM (reusing <see cref="CleanupModel"/>/<see cref="CleanupBaseUrl"/>) before synthesis,
    /// so paths, code and markdown read as natural speech. Off by default (adds a local LLM call per turn).
    /// </summary>
    public bool NaturalizeReadAloud { get; init; }

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
}
