using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;

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

        settings.IsEnabled.Should().BeFalse();
        settings.ModelName.Should().Be("large-v3-turbo");
        settings.BackendPreference.Should().Be(VoiceBackendPreference.Auto);
        settings.CleanupEnabled.Should().BeTrue();
        settings.PushToTalkKeyName.Should().Be("F9");
        settings.GlobalPushToTalk.Should().BeFalse();
        settings.AutoSubmitAfterVoice.Should().BeFalse();
        settings.TtsVoiceSid.Should().Be(1);
        settings.SttLanguage.Should().Be("auto");
        settings.InputDeviceName.Should().BeEmpty();
        settings.OutputDeviceName.Should().BeEmpty();
        settings.OpenMicEnabled.Should().BeFalse();
        settings.OpenMicSilenceTimeoutMs.Should().Be(800);
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

        loaded.IsEnabled.Should().BeTrue();
        loaded.ModelName.Should().Be("small");
        loaded.BackendPreference.Should().Be(VoiceBackendPreference.Cpu);
        loaded.CleanupEnabled.Should().BeFalse();
        loaded.AutoDetectLocalLlm.Should().BeFalse();
        loaded.LocalLlmPreference.Should().Be(LocalLlmPreference.LmStudio);
        loaded.VoiceLlmModel.Should().Be("llama3.2:3b");
        loaded.VoiceLlmBaseUrl.Should().Be("http://localhost:12345");
        loaded.PushToTalkKeyName.Should().Be("F10");
        loaded.GlobalPushToTalk.Should().BeTrue();
        loaded.AutoSubmitAfterVoice.Should().BeTrue();
        loaded.TtsVoiceSid.Should().Be(3);
        loaded.ReadAloudMode.Should().Be(ReadAloudMode.Summarized);
        loaded.ReadAloudLanguage.Should().Be("nl");
        loaded.SttLanguage.Should().Be("nl");
        loaded.InputDeviceName.Should().Be("Yeti Stereo Microphone");
        loaded.OutputDeviceName.Should().Be("Built-in Speakers");
        loaded.OpenMicEnabled.Should().BeTrue();
        loaded.OpenMicSilenceTimeoutMs.Should().Be(1200);
    }

    [Fact]
    public async Task LoadAsync_DefaultConfig_ReadAloudModeIsVerbatim()
    {
        var store = new VoiceSettingsStore(_configFilePath);

        (await store.LoadAsync()).ReadAloudMode.Should().Be(ReadAloudMode.Verbatim);
    }

    [Fact]
    public async Task LoadAsync_LegacyNaturalizeFlagOn_MigratesToNaturalizedMode()
    {
        // A config written before read-aloud gained the three-way mode carries only the old on/off flag.
        await File.WriteAllTextAsync(_configFilePath, """{ "Voice": { "NaturalizeReadAloud": true } }""");
        var store = new VoiceSettingsStore(_configFilePath);

        (await store.LoadAsync()).ReadAloudMode.Should().Be(ReadAloudMode.Naturalized);
    }

    [Fact]
    public async Task LoadAsync_LegacyNaturalizeFlagOff_MigratesToVerbatimMode()
    {
        await File.WriteAllTextAsync(_configFilePath, """{ "Voice": { "NaturalizeReadAloud": false } }""");
        var store = new VoiceSettingsStore(_configFilePath);

        (await store.LoadAsync()).ReadAloudMode.Should().Be(ReadAloudMode.Verbatim);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var voiceStore = new VoiceSettingsStore(_configFilePath);
        await voiceStore.SaveAsync(new VoiceSettings { IsEnabled = true });

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await voiceStore.LoadAsync()).IsEnabled.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
