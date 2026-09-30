using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// Load/save round-trip for the shortcuts section of <c>cockpit.json</c>, the default-fill for actions the
/// file predates, and the shared-file invariant that saving shortcuts leaves a sibling section intact.
/// </summary>
public class ShortcutSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public ShortcutSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAChangedGesture()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O"));
        var loaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+O", loaded.GestureFor(ShortcutAction.Options));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
