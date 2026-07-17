using System.Text.Json.Serialization;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="VoiceSettings"/> in the <c>voice</c> section of <c>cockpit.json</c>.</summary>
internal sealed class VoiceSettingsEntry
{
    public bool IsEnabled { get; set; }

    public string ModelName { get; set; } = "large-v3-turbo";

    /// <summary>
    /// Whether the model follows the per-machine recommendation (AC-68 slice 2). Nullable so a config written
    /// before this key existed is distinguishable from an explicit false: a missing key means the operator had
    /// hand-picked <see cref="ModelName"/> under the old free-text box, so it is kept as an explicit choice.
    /// </summary>
    public bool? ModelAutoSelected { get; set; }

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

    /// <summary>
    /// SupertonicTTS speaker id for read-aloud. The pre-Supertonic <c>TtsVoiceId</c>/<c>TtsVoiceIdDutch</c>
    /// Piper-voice keys have no meaningful mapping onto a Supertonic sid, so a config written before this key
    /// existed is simply read at the default sid (the old keys are ignored) rather than migrated.
    /// </summary>
    public int TtsVoiceSid { get; set; } = 1;

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
        ModelAutoSelected = settings.ModelAutoSelected,
        BackendPreference = settings.BackendPreference,
        CleanupEnabled = settings.CleanupEnabled,
        AutoDetectLocalLlm = settings.AutoDetectLocalLlm,
        LocalLlmPreference = settings.LocalLlmPreference,
        CleanupModel = settings.CleanupModel,
        CleanupBaseUrl = settings.CleanupBaseUrl,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        GlobalPushToTalk = settings.GlobalPushToTalk,
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice,
        TtsVoiceSid = settings.TtsVoiceSid,
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
        // A config saved before this key existed had a hand-picked model — keep it explicit rather than flipping
        // it to the recommendation behind the operator's back.
        ModelAutoSelected = ModelAutoSelected ?? false,
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
        TtsVoiceSid = TtsVoiceSid,
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
