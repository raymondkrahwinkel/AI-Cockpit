using Cockpit.Core.Notifications;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.Shortcuts;
using FluentAssertions;

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

        settings.GestureFor(ShortcutAction.NewSession).Should().Be("Shift+N");
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsAChangedGesture()
    {
        var store = new ShortcutSettingsStore(_configFilePath);

        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.Options, "Ctrl+Shift+O"));
        var loaded = await store.LoadAsync();

        loaded.GestureFor(ShortcutAction.Options).Should().Be("Ctrl+Shift+O");
    }

    [Fact]
    public async Task LoadAsync_FillsActionsMissingFromTheFileWithDefaults()
    {
        // Persist only one action, as an older/partial file would.
        var store = new ShortcutSettingsStore(_configFilePath);
        await store.SaveAsync(new ShortcutSettings(new Dictionary<ShortcutAction, string>
        {
            [ShortcutAction.NewSession] = "Ctrl+N",
        }));

        var loaded = await store.LoadAsync();

        loaded.GestureFor(ShortcutAction.NewSession).Should().Be("Ctrl+N");
        loaded.GestureFor(ShortcutAction.ToggleZoom).Should().Be(ShortcutCatalog.DefaultGesture(ShortcutAction.ToggleZoom));
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new ShortcutSettingsStore(_configFilePath);
        await store.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.About, "Shift+A"));

        (await notificationStore.LoadAsync()).WebhookUrl.Should().Be("https://example/webhook");
        (await store.LoadAsync()).GestureFor(ShortcutAction.About).Should().Be("Shift+A");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
