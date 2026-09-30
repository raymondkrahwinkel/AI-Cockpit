using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Load/save round-trip for the voice section of <c>cockpit.json</c>, plus the invariant that saving it leaves sibling sections intact.</summary>
public class VoiceSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public VoiceSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new VoiceSettingsStore(_configFilePath);

        await store.SaveAsync(new VoiceSettings
        {
            IsEnabled = true,
            ModelName = "small",
            BackendPreference = VoiceBackendPreference.Cpu,
            PushToTalkKeyName = "F10",
            GlobalPushToTalk = true,
            AutoSubmitAfterVoice = true,
            TtsVoiceSid = 3,
            TtsSpeed = 1.4,
            ReadAloudLanguage = "nl",
            SttLanguage = "nl",
            InputDeviceName = "Yeti Stereo Microphone",
            OutputDeviceName = "Built-in Speakers",
            OpenMicEnabled = true,
            OpenMicSilenceTimeoutMs = 1200,
        });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.IsEnabled);
        Assert.Equal("small", loaded.ModelName);
        Assert.Equal(VoiceBackendPreference.Cpu, loaded.BackendPreference);
        Assert.Equal("F10", loaded.PushToTalkKeyName);
        Assert.True(loaded.GlobalPushToTalk);
        Assert.True(loaded.AutoSubmitAfterVoice);
        Assert.Equal(3, loaded.TtsVoiceSid);
        Assert.Equal(1.4, loaded.TtsSpeed);
        Assert.Equal("nl", loaded.ReadAloudLanguage);
        Assert.Equal("nl", loaded.SttLanguage);
        Assert.Equal("Yeti Stereo Microphone", loaded.InputDeviceName);
        Assert.Equal("Built-in Speakers", loaded.OutputDeviceName);
        Assert.True(loaded.OpenMicEnabled);
        Assert.Equal(1200, loaded.OpenMicSilenceTimeoutMs);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
