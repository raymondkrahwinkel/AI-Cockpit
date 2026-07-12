using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.WorkingPaths;
using FluentAssertions;

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

        history.Recent.Should().BeEmpty();
        history.Favorites.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordRecentAsync_PersistsMostRecentFirst()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        await store.RecordRecentAsync(@"C:\a");
        await store.RecordRecentAsync(@"C:\b");

        var loaded = await store.LoadAsync();
        loaded.Recent.Should().Equal(@"C:\b", @"C:\a");
    }

    [Fact]
    public async Task SetFavoriteAsync_PinsAndUnpins_AndRoundTrips()
    {
        var store = new WorkingPathHistoryStore(_configFilePath);

        var pinned = await store.SetFavoriteAsync(@"C:\fav", favorite: true);
        pinned.IsFavorite(@"C:\fav").Should().BeTrue();
        (await store.LoadAsync()).Favorites.Should().Equal(@"C:\fav");

        await store.SetFavoriteAsync(@"C:\fav", favorite: false);
        (await store.LoadAsync()).Favorites.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordRecentAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new WorkingPathHistoryStore(_configFilePath);
        await store.RecordRecentAsync(@"C:\project");

        (await notificationStore.LoadAsync()).WebhookUrl.Should().Be("https://example/webhook");
        (await store.LoadAsync()).Recent.Should().Equal(@"C:\project");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
