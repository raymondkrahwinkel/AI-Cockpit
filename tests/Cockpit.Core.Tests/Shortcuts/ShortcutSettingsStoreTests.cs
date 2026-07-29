using Cockpit.Core.Notifications;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Notifications;
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
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.Equal("Ctrl+N", settings.GestureFor(ShortcutAction.NewSession));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAChangedGesture()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O"));
        var loaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Shift+O", loaded.GestureFor(ShortcutAction.Options));
    }

    [Fact]
    public async Task LoadAsync_FillsActionsMissingFromTheFileWithDefaults()
    {
        // Persist only one action, as an older/partial file would.
        var store = new ShortcutSettingsStore(_configFilePath);
        await store.SaveAsync(new ShortcutSettings(
            new Dictionary<ShortcutAction, string> { [ShortcutAction.NewSession] = "Ctrl+Alt+N" },
            new Dictionary<string, string>()));

        var loaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Alt+N", loaded.GestureFor(ShortcutAction.NewSession));
        Assert.Equal(ShortcutCatalog.DefaultGesture(ShortcutAction.ToggleZoom), loaded.GestureFor(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAPluginGestureOverride()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(ShortcutSettings.Default.WithPlugin("youtrack.open", "Ctrl+Y"));
        var loaded = await store.LoadAsync();

        Assert.Equal("Ctrl+Y", loaded.GestureForPlugin("youtrack.open", "Shift+Y"));
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new ShortcutSettingsStore(_configFilePath);
        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.About, "Shift+A"));

        Assert.Equal("https://example/webhook", (await notificationStore.LoadAsync()).WebhookUrl);
        Assert.Equal("Shift+A", (await store.LoadAsync()).GestureFor(ShortcutAction.About));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
