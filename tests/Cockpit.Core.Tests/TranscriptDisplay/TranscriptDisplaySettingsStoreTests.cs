using Cockpit.Core.SessionSwitching;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.SessionSwitching;
using Cockpit.Infrastructure.TranscriptDisplay;
using FluentAssertions;

namespace Cockpit.Core.Tests.TranscriptDisplay;

/// <summary>
/// Load/save round-trip for the transcript-display section of <c>cockpit.json</c>, plus the invariant
/// that saving it leaves the sibling sections (notifications, session switching) intact — all stores
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

        var switchStore = new SessionSwitchSettingsStore(_configFilePath);
        await switchStore.SaveAsync(new SessionSwitchSettings { IsEnabled = false, Modifier = SessionSwitchModifier.Alt });

        var displayStore = new TranscriptDisplaySettingsStore(_configFilePath);
        await displayStore.SaveAsync(new TranscriptDisplaySettings { ShowTimestamps = true });

        var reloadedNotifications = await notificationStore.LoadAsync();
        var reloadedSwitch = await switchStore.LoadAsync();
        var reloadedDisplay = await displayStore.LoadAsync();

        reloadedNotifications.WebhookUrl.Should().Be("https://example/webhook");
        reloadedSwitch.IsEnabled.Should().BeFalse();
        reloadedSwitch.Modifier.Should().Be(SessionSwitchModifier.Alt);
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
