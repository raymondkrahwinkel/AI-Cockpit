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

    /// <summary>Model id for the shared voice-LLM step. Null when neither this nor a legacy key was written; migrated/defaulted in <see cref="ToDomain"/>.</summary>
    public string? VoiceLlmModel { get; set; }

    /// <summary>OpenAI-compatible local LLM base URL (Ollama/LM Studio) for the shared voice-LLM step. Null when neither this nor a legacy key was written.</summary>
    public string? VoiceLlmBaseUrl { get; set; }

    /// <summary>
    /// Legacy on-disk keys from before the cleanup config was renamed to the shared, provider-neutral voice-LLM
    /// config: <see cref="CleanupModel"/>/<see cref="CleanupBaseUrl"/> were the previous names, and
    /// <see cref="OllamaBaseUrl"/> the Ollama-specific one before that. All are read to migrate an existing config
    /// but never written back (see <see cref="FromDomain"/>), so once the file is next saved only the
    /// <see cref="VoiceLlmModel"/>/<see cref="VoiceLlmBaseUrl"/> keys persist.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CleanupModel { get; set; }

    /// <inheritdoc cref="CleanupModel"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CleanupBaseUrl { get; set; }

    /// <inheritdoc cref="CleanupModel"/>
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

    /// <summary>How read-aloud renders a reply (#35). Null when neither this nor the legacy naturalize flag was written.</summary>
    public ReadAloudMode? ReadAloudMode { get; set; }

    /// <summary>
    /// Legacy on-disk on/off naturalize flag from before read-aloud gained the three-way mode. Read to migrate an
    /// existing config (<c>true</c> → Naturalized, otherwise Verbatim); never written back (see <see cref="FromDomain"/>),
    /// so once the file is next saved the <see cref="ReadAloudMode"/> key is what persists.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NaturalizeReadAloud { get; set; }

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
        VoiceLlmModel = settings.VoiceLlmModel,
        VoiceLlmBaseUrl = settings.VoiceLlmBaseUrl,
        // Legacy keys are migration-only: never written back, so they stay null and drop out of the file.
        CleanupModel = null,
        CleanupBaseUrl = null,
        OllamaBaseUrl = null,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        GlobalPushToTalk = settings.GlobalPushToTalk,
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice,
        TtsVoiceSid = settings.TtsVoiceSid,
        SttLanguage = settings.SttLanguage,
        InputDeviceName = settings.InputDeviceName,
        OutputDeviceName = settings.OutputDeviceName,
        OpenMicEnabled = settings.OpenMicEnabled,
        OpenMicSilenceTimeoutMs = settings.OpenMicSilenceTimeoutMs,
        StopReadAloudWhenSpeaking = settings.StopReadAloudWhenSpeaking,
        StopReadAloudLevelThreshold = settings.StopReadAloudLevelThreshold,
        ReadAloudMode = settings.ReadAloudMode,
        // Legacy naturalize flag is migration-only: never written back, so it stays null and drops out of the file.
        NaturalizeReadAloud = null,
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
        // Prefer the neutral key; fall back through the renamed-cleanup key and default so an existing config migrates cleanly.
        VoiceLlmModel = VoiceLlmModel ?? CleanupModel ?? "gemma3:4b",
        // Prefer the neutral key; fall back through the renamed-cleanup and older Ollama keys so an existing config migrates cleanly.
        VoiceLlmBaseUrl = VoiceLlmBaseUrl ?? CleanupBaseUrl ?? OllamaBaseUrl ?? "http://localhost:11434",
        PushToTalkKeyName = PushToTalkKeyName,
        GlobalPushToTalk = GlobalPushToTalk,
        AutoSubmitAfterVoice = AutoSubmitAfterVoice,
        TtsVoiceSid = TtsVoiceSid,
        SttLanguage = SttLanguage,
        InputDeviceName = InputDeviceName,
        OutputDeviceName = OutputDeviceName,
        // Prefer the three-way key; fall back to the legacy on/off naturalize flag so an existing config keeps its
        // behaviour (naturalize on → Naturalized, otherwise Verbatim) rather than silently resetting to Verbatim.
        ReadAloudMode = ReadAloudMode
            ?? (NaturalizeReadAloud == true ? Cockpit.Core.Voice.ReadAloudMode.Naturalized : Cockpit.Core.Voice.ReadAloudMode.Verbatim),
        OpenMicEnabled = OpenMicEnabled,
        OpenMicSilenceTimeoutMs = OpenMicSilenceTimeoutMs,
        StopReadAloudWhenSpeaking = StopReadAloudWhenSpeaking,
        StopReadAloudLevelThreshold = StopReadAloudLevelThreshold,
    };
}
