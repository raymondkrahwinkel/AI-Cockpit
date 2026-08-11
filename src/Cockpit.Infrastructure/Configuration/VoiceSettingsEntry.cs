using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `VoiceSettings` in the `voice` section of `cockpit.json`.
internal sealed class VoiceSettingsEntry
{
    public bool IsEnabled { get; set; }

    public string ModelName { get; set; } = "large-v3-turbo";

    // Whether the model follows the per-machine recommendation (AC-68 slice 2). Nullable so a config written
    // before this key existed is distinguishable from an explicit false: a missing key means the operator had
    // hand-picked `ModelName` under the old free-text box, so it is kept as an explicit choice.
    public bool? ModelAutoSelected { get; set; }

    public VoiceBackendPreference BackendPreference { get; set; } = VoiceBackendPreference.Auto;

    public string PushToTalkKeyName { get; set; } = "F9";

    public bool GlobalPushToTalk { get; set; }

    public bool AutoSubmitAfterVoice { get; set; }

    // SupertonicTTS speaker id for read-aloud. The pre-Supertonic `TtsVoiceId`/`TtsVoiceIdDutch`
    // Piper-voice keys have no meaningful mapping onto a Supertonic sid, so a config written before this key
    // existed is simply read at the default sid (the old keys are ignored) rather than migrated.
    public int TtsVoiceSid { get; set; } = 1;

    // Read-aloud speaking rate (AC-708). New key; defaults to 1.0 (natural pace) for an existing config.
    public double TtsSpeed { get; set; } = 1.0;

    // Preferred base language for read-aloud ("en"/"nl"). New key; defaults to "en" for an existing config.
    public string ReadAloudLanguage { get; set; } = "en";

    public string SttLanguage { get; set; } = "auto";

    public string InputDeviceName { get; set; } = "";

    public string OutputDeviceName { get; set; } = "";

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
        PushToTalkKeyName = settings.PushToTalkKeyName,
        GlobalPushToTalk = settings.GlobalPushToTalk,
        AutoSubmitAfterVoice = settings.AutoSubmitAfterVoice,
        TtsVoiceSid = settings.TtsVoiceSid,
        TtsSpeed = settings.TtsSpeed,
        ReadAloudLanguage = settings.ReadAloudLanguage,
        SttLanguage = settings.SttLanguage,
        InputDeviceName = settings.InputDeviceName,
        OutputDeviceName = settings.OutputDeviceName,
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
        PushToTalkKeyName = PushToTalkKeyName,
        GlobalPushToTalk = GlobalPushToTalk,
        AutoSubmitAfterVoice = AutoSubmitAfterVoice,
        TtsVoiceSid = TtsVoiceSid,
        TtsSpeed = TtsSpeed,
        ReadAloudLanguage = ReadAloudLanguage,
        SttLanguage = SttLanguage,
        InputDeviceName = InputDeviceName,
        OutputDeviceName = OutputDeviceName,
        OpenMicEnabled = OpenMicEnabled,
        OpenMicSilenceTimeoutMs = OpenMicSilenceTimeoutMs,
        StopReadAloudWhenSpeaking = StopReadAloudWhenSpeaking,
        StopReadAloudLevelThreshold = StopReadAloudLevelThreshold,
    };
}
