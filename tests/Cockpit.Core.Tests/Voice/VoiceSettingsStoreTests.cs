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
            CleanupModel = "llama3.2:3b",
            OllamaBaseUrl = "http://localhost:12345",
            PushToTalkKeyName = "F10",
        });
        var loaded = await store.LoadAsync();

        loaded.IsEnabled.Should().BeTrue();
        loaded.ModelName.Should().Be("small");
        loaded.BackendPreference.Should().Be(VoiceBackendPreference.Cpu);
        loaded.CleanupEnabled.Should().BeFalse();
        loaded.CleanupModel.Should().Be("llama3.2:3b");
        loaded.OllamaBaseUrl.Should().Be("http://localhost:12345");
        loaded.PushToTalkKeyName.Should().Be("F10");
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
