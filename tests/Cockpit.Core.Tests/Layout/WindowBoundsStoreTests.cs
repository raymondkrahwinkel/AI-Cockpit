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
    public async Task LoadAsync_NoConfigFile_ReturnsNull()
    {
        var store = new WindowBoundsStore(_configFilePath);

        Assert.Null((await store.LoadAsync("main")));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var store = new WindowBoundsStore(_configFilePath);
        var bounds = new WindowBounds(120, 80, 1400, 900, IsMaximized: true);

        await store.SaveAsync("main", bounds);

        Assert.Equal(bounds, (await store.LoadAsync("main")));
    }

    [Fact]
    public async Task SaveAsync_KeepsEachKeysBoundsSeparate()
    {
        var store = new WindowBoundsStore(_configFilePath);
        var mainBounds = new WindowBounds(0, 0, 1280, 820, IsMaximized: false);
        var assistantBounds = new WindowBounds(200, 150, 520, 640, IsMaximized: false);

        await store.SaveAsync("main", mainBounds);
        await store.SaveAsync("assistant", assistantBounds);

        Assert.Equal(mainBounds, (await store.LoadAsync("main")));
        Assert.Equal(assistantBounds, (await store.LoadAsync("assistant")));
    }

    [Fact]
    public async Task LoadAsync_UnknownKey_ReturnsNull()
    {
        var store = new WindowBoundsStore(_configFilePath);
        await store.SaveAsync("main", new WindowBounds(0, 0, 1280, 820, IsMaximized: false));

        Assert.Null(await store.LoadAsync("assistant"));
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

    [Theory]
    [InlineData(300, false)]
    [InlineData(400, true)]
    [InlineData(1280, true)]
    public void HasUsableSize_GuardsAgainstDegenerateSizes(int size, bool expected)
        => Assert.Equal(expected, new WindowBounds(0, 0, size, size, false).HasUsableSize);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
