using System.Text.Json.Serialization;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="VoiceSettings"/> in the <c>voice</c> section of <c>cockpit.json</c>.</summary>
internal sealed class VoiceSettingsEntry
{
    public bool IsEnabled { get; set; }

    public string ModelName { get; set; } = "large-v3-turbo";

    public VoiceBackendPreference BackendPreference { get; set; } = VoiceBackendPreference.Auto;

    public bool CleanupEnabled { get; set; } = true;

    public bool AutoDetectLocalLlm { get; set; } = true;

    public LocalLlmPreference LocalLlmPreference { get; set; } = LocalLlmPreference.Auto;

    public string CleanupModel { get; set; } = "qwen2.5:3b-instruct";

    /// <summary>OpenAI-compatible local LLM base URL (Ollama/LM Studio). Null when neither this nor the legacy key was written.</summary>
    public string? CleanupBaseUrl { get; set; }

    /// <summary>
    /// Legacy on-disk key from before the Ollama-specific cleanup was generalized to any OpenAI-compatible
    /// server. Read to migrate an existing config; never written back (see <see cref="FromDomain"/>), so once
    /// the file is next saved the neutral <see cref="CleanupBaseUrl"/> key is what persists.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OllamaBaseUrl { get; set; }

    public string PushToTalkKeyName { get; set; } = "F9";

    public bool GlobalPushToTalk { get; set; }

    public bool AutoSubmitAfterVoice { get; set; }

    public string TtsVoiceId { get; set; } = "en_US-lessac-medium";

    public string TtsVoiceIdDutch { get; set; } = "nl_NL-ronnie-medium";

    public string SttLanguage { get; set; } = "auto";

    public string InputDeviceName { get; set; } = "";

    public string OutputDeviceName { get; set; } = "";

    public bool NaturalizeReadAloud { get; set; }

    public bool OpenMicEnabled { get; set; }

    public int OpenMicSilenceTimeoutMs { get; set; } = 800;

    public bool StopReadAloudWhenSpeaking { get; set; }

    public double StopReadAloudLevelThreshold { get; set; } = 0.15;

    public static VoiceSettingsEntry FromDomain(VoiceSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        ModelName = settings.ModelName,
        BackendPreference = settings.BackendPreference,
        CleanupEnabled = settings.CleanupEnabled,
        AutoDetectLocalLlm = settings.AutoDetectLocalLlm,
        LocalLlmPreference = settings.LocalLlmPreference,
        CleanupModel = settings.CleanupModel,
        CleanupBaseUrl = settings.CleanupBaseUrl,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        GlobalPushToTalk = settings.GlobalPushToTalk,
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice,
        TtsVoiceId = settings.TtsVoiceId,
        TtsVoiceIdDutch = settings.TtsVoiceIdDutch,
        SttLanguage = settings.SttLanguage,
        InputDeviceName = settings.InputDeviceName,
        OutputDeviceName = settings.OutputDeviceName,
        NaturalizeReadAloud = settings.NaturalizeReadAloud,
        OpenMicEnabled = settings.OpenMicEnabled,
        OpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs,
        StopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking,
        StopReadAloudLevelThreshold = settings.StopReadAloudLevelThreshold,
    };

    public VoiceSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        ModelName = ModelName,
        BackendPreference = BackendPreference,
        CleanupEnabled = CleanupEnabled,
        AutoDetectLocalLlm = AutoDetectLocalLlm,
        LocalLlmPreference = LocalLlmPreference,
        CleanupModel = CleanupModel,
        // Prefer the neutral key; fall back to the legacy Ollama key so an existing config migrates cleanly.
        CleanupBaseUrl = CleanupBaseUrl ?? OllamaBaseUrl ?? "http://localhost:11434",
        PushToTalkKeyName = PushToTalkKeyName,
        GlobalPushToTalk = GlobalPushToTalk,
        AutoSubmitAfterVoice = AutoSubmitAfterVoice,
        TtsVoiceId = TtsVoiceId,
        TtsVoiceIdDutch = TtsVoiceIdDutch,
        SttLanguage = SttLanguage,
        InputDeviceName = InputDeviceName,
        OutputDeviceName = OutputDeviceName,
        NaturalizeReadAloud = NaturalizeReadAloud,
        OpenMicEnabled = OpenMicEnabled,
        OpenMicSilenceTimeoutMs = OpenMicSilenceTimeoutMs,
        StopReadAloudWhenSpeaking = StopReadAloudWhenSpeaking,
        StopReadAloudLevelThreshold = StopReadAloudLevelThreshold,
    };
}
