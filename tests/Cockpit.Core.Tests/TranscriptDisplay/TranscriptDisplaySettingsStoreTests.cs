using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.Shortcuts;
using Cockpit.Core.Notifications;
using Cockpit.Core.Shortcuts;
using Cockpit.Infrastructure.TranscriptDisplay;
using FluentAssertions;

namespace Cockpit.Core.Tests.TranscriptDisplay;

/// <summary>
/// Load/save round-trip for the transcript-display section of <c>cockpit.json</c>, plus the invariant
/// that saving it leaves the sibling sections (notifications, shortcuts) intact — all stores
/// share the one file.
/// </summary>
public class TranscriptDisplaySettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public TranscriptDisplaySettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new TranscriptDisplaySettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.ShowTimestamps.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new TranscriptDisplaySettingsStore(_configFilePath);

        await store.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });
        var loaded = await store.LoadAsync();

        loaded.ShowTimestamps.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var shortcutStore = new ShortcutSettingsStore(_configFilePath);
        await shortcutStore.SaveAsync(ShortcutSettings.Default.With(ShortcutAction.NextSession, "Alt+Down"));

        var displayStore = new TranscriptDisplaySettingsStore(_configFilePath);
        await displayStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });

        var reloadedNotifications = await notificationStore.LoadAsync();
        var reloadedShortcuts = await shortcutStore.LoadAsync();
        var reloadedDisplay = await displayStore.LoadAsync();

        reloadedNotifications.WebhookUrl.Should().Be("https://example/webhook");
        reloadedShortcuts.GestureFor(ShortcutAction.NextSession).Should().Be("Alt+Down");
        reloadedDisplay.ShowTimestamps.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
