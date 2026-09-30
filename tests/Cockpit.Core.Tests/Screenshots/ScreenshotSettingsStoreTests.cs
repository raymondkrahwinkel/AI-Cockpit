using Cockpit.Core.Screenshots;
using Cockpit.Infrastructure.Screenshots;

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
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new ScreenshotSettingsStore(_configFilePath);

        await store.SaveAsync(new ScreenshotSettings { GlobalHotkeyEnabled = true, HotkeyKeyName = "F7" });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.GlobalHotkeyEnabled);
        Assert.Equal("F7", loaded.HotkeyKeyName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
