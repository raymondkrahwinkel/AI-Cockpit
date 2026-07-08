using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="VoiceSettings"/> in the <c>voice</c> section of <c>cockpit.json</c>.</summary>
internal sealed class VoiceSettingsEntry
{
    public bool IsEnabled { get; set; }

    public string ModelName { get; set; } = "large-v3-turbo";

    public VoiceBackendPreference BackendPreference { get; set; } = VoiceBackendPreference.Auto;

    public bool CleanupEnabled { get; set; } = true;

    public string CleanupModel { get; set; } = "qwen2.5:3b-instruct";

    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    public string PushToTalkKeyName { get; set; } = "F9";

    public bool GlobalPushToTalk { get; set; }

    public bool AutoSubmitAfterVoice { get; set; }

    public string TtsVoiceId { get; set; } = "en_US-lessac-medium";

    public string SttLanguage { get; set; } = "auto";

    public static VoiceSettingsEntry FromDomain(VoiceSettings settings) => new()
    {
        IsEnabled = settings.IsEnabled,
        ModelName = settings.ModelName,
        BackendPreference = settings.BackendPreference,
        CleanupEnabled = settings.CleanupEnabled,
        CleanupModel = settings.CleanupModel,
        OllamaBaseUrl = settings.OllamaBaseUrl,
        PushToTalkKeyName = settings.PushToTalkKeyName,
        GlobalPushToTalk = settings.GlobalPushToTalk,
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice,
        TtsVoiceId = settings.TtsVoiceId,
        SttLanguage = settings.SttLanguage,
    };

    public VoiceSettings ToDomain() => new()
    {
        IsEnabled = IsEnabled,
        ModelName = ModelName,
        BackendPreference = BackendPreference,
        CleanupEnabled = CleanupEnabled,
        CleanupModel = CleanupModel,
        OllamaBaseUrl = OllamaBaseUrl,
        PushToTalkKeyName = PushToTalkKeyName,
        GlobalPushToTalk = GlobalPushToTalk,
        AutoSubmitAfterVoice = AutoSubmitAfterVoice,
        TtsVoiceId = TtsVoiceId,
        SttLanguage = SttLanguage,
    };
}
