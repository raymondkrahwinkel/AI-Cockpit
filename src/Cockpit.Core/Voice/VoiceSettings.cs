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
}
