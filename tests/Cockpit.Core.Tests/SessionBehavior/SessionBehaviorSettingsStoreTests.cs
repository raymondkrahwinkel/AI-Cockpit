using Cockpit.Core.Diagnostics;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.SessionBehavior;
using Cockpit.Infrastructure.TranscriptDisplay;

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

        Assert.False(settings.AutoCloseOnExit);
        Assert.False(settings.CombineQueuedMessages);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new SessionBehaviorSettingsStore(_configFilePath);

        await store.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true, CombineQueuedMessages = true });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.AutoCloseOnExit);
        Assert.True(loaded.CombineQueuedMessages);
    }

    [Fact]
    public async Task LoadAsync_ASectionWrittenBeforeTheSharedBudgetExisted_ReadsTheDefaultRatherThanZero()
    {
        // AC-1086: absent would deserialise as 0, and a budget of nothing warns on an idle cockpit — for every
        // install that predates the setting, which is all of them.
        await File.WriteAllTextAsync(_configFilePath, """{"sessionBehavior":{"autoCloseOnExit":true}}""");

        var settings = await new SessionBehaviorSettingsStore(_configFilePath).LoadAsync();

        Assert.Equal(MemoryPressure.DefaultBudgetPercent, settings.MemoryBudgetPercent);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTheSharedMemoryBudget()
    {
        var store = new SessionBehaviorSettingsStore(_configFilePath);

        await store.SaveAsync(new SessionBehaviorSettings { MemoryBudgetPercent = 55 });

        Assert.Equal(55, (await store.LoadAsync()).MemoryBudgetPercent);
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

        Assert.Equal("https://example/webhook", reloadedNotifications.WebhookUrl);
        Assert.True(reloadedDisplay.ShowTimestamps);
        Assert.True(reloadedBehavior.AutoCloseOnExit);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
