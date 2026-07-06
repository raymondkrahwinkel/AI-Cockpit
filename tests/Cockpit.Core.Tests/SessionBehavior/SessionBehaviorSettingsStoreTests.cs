using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.SessionBehavior;
using Cockpit.Infrastructure.TranscriptDisplay;
using FluentAssertions;

namespace Cockpit.Core.Tests.SessionBehavior;

/// <summary>
/// Load/save round-trip for the session-behaviour section of <c>cockpit.json</c>, plus the invariant
/// that saving it leaves the sibling sections (notifications, transcript display) intact — all stores
/// share the one file.
/// </summary>
public class SessionBehaviorSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public SessionBehaviorSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new SessionBehaviorSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.AutoCloseOnExit.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new SessionBehaviorSettingsStore(_configFilePath);

        await store.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true });
        var loaded = await store.LoadAsync();

        loaded.AutoCloseOnExit.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var displayStore = new TranscriptDisplaySettingsStore(_configFilePath);
        await displayStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });

        var behaviorStore = new SessionBehaviorSettingsStore(_configFilePath);
        await behaviorStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true });

        var reloadedNotifications = await notificationStore.LoadAsync();
        var reloadedDisplay = await displayStore.LoadAsync();
        var reloadedBehavior = await behaviorStore.LoadAsync();

        reloadedNotifications.WebhookUrl.Should().Be("https://example/webhook");
        reloadedDisplay.ShowTimestamps.Should().BeTrue();
        reloadedBehavior.AutoCloseOnExit.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
