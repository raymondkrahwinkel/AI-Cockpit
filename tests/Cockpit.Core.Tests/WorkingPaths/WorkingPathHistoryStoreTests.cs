using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.WorkingPaths;

namespace Cockpit.Core.Tests.WorkingPaths;

/// <summary>
/// Load/save round-trip for the working-paths section of <c>cockpit.json</c>, plus the shared-file invariant
/// that recording a path leaves a sibling section (notifications) intact.
/// </summary>
public class WorkingPathHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public WorkingPathHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsEmpty()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        var history = await store.LoadAsync();

        Assert.Empty(history.Recent);
        Assert.Empty(history.Favorites);
    }

    [Fact]
    public async Task RecordRecentAsync_PersistsMostRecentFirst()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        await store.RecordRecentAsync(@"C:\a");
        await store.RecordRecentAsync(@"C:\b");

        var loaded = await store.LoadAsync();
        Assert.Equal(new[] { @"C:\b", @"C:\a" }, loaded.Recent);
    }

    [Fact]
    public async Task SetFavoriteAsync_PinsAndUnpins_AndRoundTrips()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        var pinned = await store.SetFavoriteAsync(@"C:\fav", favorite: true);
        Assert.True(pinned.IsFavorite(@"C:\fav"));
        Assert.Equal(new[] { @"C:\fav" }, (await store.LoadAsync()).Favorites);

        await store.SetFavoriteAsync(@"C:\fav", favorite: false);
        Assert.Empty((await store.LoadAsync()).Favorites);
    }

    [Fact]
    public async Task RemoveAsync_ForgetsThePathFromBothListsAndRoundTrips()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);
        await store.RecordRecentAsync(@"C:\a");
        await store.SetFavoriteAsync(@"C:\a", favorite: true);
        await store.RecordRecentAsync(@"C:\b");

        await store.RemoveAsync(@"C:\a");

        var loaded = await store.LoadAsync();
        Assert.Equal(new[] { @"C:\b" }, loaded.Recent);
        Assert.Empty(loaded.Favorites);
    }

    [Fact]
    public async Task RecordRecentAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new WorkingPathHistoryStore(_configFilePath);
        await store.RecordRecentAsync(@"C:\project");

        Assert.Equal("https://example/webhook", (await notificationStore.LoadAsync()).WebhookUrl);
        Assert.Equal(new[] { @"C:\project" }, (await store.LoadAsync()).Recent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
