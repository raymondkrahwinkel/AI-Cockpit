using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Layout;
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
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new VoiceSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.False(settings.IsEnabled);
        Assert.Equal("large-v3-turbo", settings.ModelName);
        Assert.Equal(VoiceBackendPreference.Auto, settings.BackendPreference);
        Assert.True(settings.CleanupEnabled);
        Assert.Equal("F9", settings.PushToTalkKeyName);
        Assert.False(settings.GlobalPushToTalk);
        Assert.False(settings.AutoSubmitAfterVoice);
        Assert.Equal(1, settings.TtsVoiceSid);
        Assert.Equal("auto", settings.SttLanguage);
        Assert.Empty(settings.InputDeviceName);
        Assert.Empty(settings.OutputDeviceName);
        Assert.False(settings.OpenMicEnabled);
        Assert.Equal(800, settings.OpenMicSilenceTimeoutMs);
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
            CleanupEnabled = false,
            AutoDetectLocalLlm = false,
            LocalLlmPreference = LocalLlmPreference.LmStudio,
            VoiceLlmModel = "llama3.2:3b",
            VoiceLlmBaseUrl = "http://localhost:12345",
            PushToTalkKeyName = "F10",
            GlobalPushToTalk = true,
            AutoSubmitAfterVoice = true,
            TtsVoiceSid = 3,
            ReadAloudMode = ReadAloudMode.Summarized,
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
        Assert.False(loaded.CleanupEnabled);
        Assert.False(loaded.AutoDetectLocalLlm);
        Assert.Equal(LocalLlmPreference.LmStudio, loaded.LocalLlmPreference);
        Assert.Equal("llama3.2:3b", loaded.VoiceLlmModel);
        Assert.Equal("http://localhost:12345", loaded.VoiceLlmBaseUrl);
        Assert.Equal("F10", loaded.PushToTalkKeyName);
        Assert.True(loaded.GlobalPushToTalk);
        Assert.True(loaded.AutoSubmitAfterVoice);
        Assert.Equal(3, loaded.TtsVoiceSid);
        Assert.Equal(ReadAloudMode.Summarized, loaded.ReadAloudMode);
        Assert.Equal("nl", loaded.ReadAloudLanguage);
        Assert.Equal("nl", loaded.SttLanguage);
        Assert.Equal("Yeti Stereo Microphone", loaded.InputDeviceName);
        Assert.Equal("Built-in Speakers", loaded.OutputDeviceName);
        Assert.True(loaded.OpenMicEnabled);
        Assert.Equal(1200, loaded.OpenMicSilenceTimeoutMs);
    }

    [Fact]
    public async Task LoadAsync_DefaultConfig_ReadAloudModeIsVerbatim()
    {
        var store = new VoiceSettingsStore(_configFilePath);

        Assert.Equal(ReadAloudMode.Verbatim, (await store.LoadAsync()).ReadAloudMode);
    }

    [Fact]
    public async Task LoadAsync_LegacyNaturalizeFlagOn_MigratesToNaturalizedMode()
    {
        // A config written before read-aloud gained the three-way mode carries only the old on/off flag.
        await File.WriteAllTextAsync(_configFilePath, """{ "Voice": { "NaturalizeReadAloud": true } }""");
        var store = new VoiceSettingsStore(_configFilePath);

        Assert.Equal(ReadAloudMode.Naturalized, (await store.LoadAsync()).ReadAloudMode);
    }

    [Fact]
    public async Task LoadAsync_LegacyNaturalizeFlagOff_MigratesToVerbatimMode()
    {
        await File.WriteAllTextAsync(_configFilePath, """{ "Voice": { "NaturalizeReadAloud": false } }""");
        var store = new VoiceSettingsStore(_configFilePath);

        Assert.Equal(ReadAloudMode.Verbatim, (await store.LoadAsync()).ReadAloudMode);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var voiceStore = new VoiceSettingsStore(_configFilePath);
        await voiceStore.SaveAsync(new VoiceSettings { IsEnabled = true });

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        Assert.True((await voiceStore.LoadAsync()).IsEnabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
