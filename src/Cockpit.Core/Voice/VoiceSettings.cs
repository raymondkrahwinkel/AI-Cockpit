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

    public string CleanupModel { get; init; } = "qwen2.5:3b-instruct";

    public string OllamaBaseUrl { get; init; } = "http://localhost:11434";

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
    /// first use the same way the Whisper model is.
    /// </summary>
    public string TtsVoiceId { get; init; } = "en_US-lessac-medium";

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
    /// local Ollama model (reusing <see cref="CleanupModel"/>/<see cref="OllamaBaseUrl"/>) before synthesis,
    /// so paths, code and markdown read as natural speech. Off by default (adds a local LLM call per turn).
    /// </summary>
    public bool NaturalizeReadAloud { get; init; }
}
