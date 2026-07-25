using FluentAssertions;
using Cockpit.Core.Screenshots;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Screenshots;
using Cockpit.Infrastructure.Voice;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>Load/save round-trip for the screenshots section of <c>cockpit.json</c>, plus the invariant that saving it leaves sibling sections intact.</summary>
public class ScreenshotSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ScreenshotSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new ScreenshotSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.GlobalHotkeyEnabled.Should().BeFalse("a desktop-wide key is taken from every other application, so it is opted into");
        settings.HotkeyKeyName.Should().Be("F8");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new ScreenshotSettingsStore(_configFilePath);

        await store.SaveAsync(new ScreenshotSettings { GlobalHotkeyEnabled = true, HotkeyKeyName = "F7" });
        var loaded = await store.LoadAsync();

        loaded.GlobalHotkeyEnabled.Should().BeTrue();
        loaded.HotkeyKeyName.Should().Be("F7");
    }

    /// <summary>
    /// Every store rewrites the whole file, so one that dropped another's section would lose settings the
    /// operator never touched — the reason each of them goes through CockpitConfigFileAccess rather than
    /// serialising its own object over the file.
    /// </summary>
    [Fact]
    public async Task SaveAsync_LeavesSiblingSectionsIntact()
    {
        await new VoiceSettingsStore(_configFilePath).SaveAsync(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F10" });

        await new ScreenshotSettingsStore(_configFilePath).SaveAsync(new ScreenshotSettings { GlobalHotkeyEnabled = true });

        var voice = await new VoiceSettingsStore(_configFilePath).LoadAsync();
        voice.IsEnabled.Should().BeTrue();
        voice.PushToTalkKeyName.Should().Be("F10");
    }

    /// <summary>
    /// A blank key in the file arms nothing and reports nothing, which reads as a broken hotkey rather than an
    /// unset one. Hand-edited config and a half-written save both produce it.
    /// </summary>
    [Fact]
    public async Task AnEmptyKeyInTheFile_FallsBackToTheDefault()
    {
        await File.WriteAllTextAsync(_configFilePath, """{"screenshots":{"globalHotkeyEnabled":true,"hotkeyKeyName":"  "}}""");

        var settings = await new ScreenshotSettingsStore(_configFilePath).LoadAsync();

        settings.HotkeyKeyName.Should().Be("F8");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
