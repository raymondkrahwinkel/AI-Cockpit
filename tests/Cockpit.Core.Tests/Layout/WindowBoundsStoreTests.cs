using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Notifications;

namespace Cockpit.Core.Tests.Layout;

/// <summary>
/// Load/save round-trip for the (AC-866) keyed window-bounds section of <c>cockpit.json</c>, the
/// null-when-unset case, reading the pre-AC-866 flat form as <c>"main"</c>, and the shared-file invariant that
/// saving bounds leaves a sibling section intact.
/// </summary>
public class WindowBoundsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public WindowBoundsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_PreAc866FlatForm_ReadsAsMain()
    {
        await File.WriteAllTextAsync(_configFilePath,
            """{"WindowBounds":{"X":10,"Y":20,"Width":1200,"Height":800,"IsMaximized":false}}""");

        var store = new WindowBoundsStore(_configFilePath);

        Assert.Equal(new WindowBounds(10, 20, 1200, 800, IsMaximized: false), await store.LoadAsync("main"));
    }

    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new WindowBoundsStore(_configFilePath);
        await store.SaveAsync("main", new WindowBounds(0, 0, 1280, 820, IsMaximized: false));

        Assert.Equal("https://example/webhook", (await notificationStore.LoadAsync()).WebhookUrl);
        Assert.Equal(1280, (await store.LoadAsync("main"))!.Width);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
