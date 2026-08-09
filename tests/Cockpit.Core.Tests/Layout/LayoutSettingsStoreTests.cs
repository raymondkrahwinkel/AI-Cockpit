using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.SessionBehavior;

namespace Cockpit.Core.Tests.Layout;

/// <summary>
/// Load/save round-trip for the layout section of <c>cockpit.json</c>, plus the invariant that saving
/// it leaves the sibling sections (notifications, session behaviour) intact.
/// </summary>
public class LayoutSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public LayoutSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new LayoutSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        Assert.False(settings.SingleSessionLayout);
        Assert.Equal(LayoutSettings.DefaultSidebarWidth, settings.SidebarWidth);
        Assert.Equal(LayoutSettings.DefaultFocusRailWeight, settings.FocusRailWeight);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new LayoutSettingsStore(_configFilePath);

        await store.SaveAsync(new LayoutSettings { SingleSessionLayout = true, StackSessionsVertically = true, FocusRailLayout = true, MinimizeToTrayOnClose = true, SidebarWidth = 260, FocusRailWeight = 0.5 });
        var loaded = await store.LoadAsync();

        Assert.True(loaded.SingleSessionLayout);
        Assert.True(loaded.StackSessionsVertically);
        Assert.True(loaded.FocusRailLayout);
        Assert.True(loaded.MinimizeToTrayOnClose);
        Assert.Equal(260, loaded.SidebarWidth);
        Assert.Equal(0.5, loaded.FocusRailWeight);
    }

    [Theory]
    [InlineData(50, LayoutSettings.MinSidebarWidth)]
    [InlineData(900, LayoutSettings.MaxSidebarWidth)]
    public async Task SaveAsync_ClampsAnOutOfRangeSidebarWidth(double requested, double expected)
    {
        var store = new LayoutSettingsStore(_configFilePath);

        await store.SaveAsync(new LayoutSettings { SidebarWidth = requested });
        var loaded = await store.LoadAsync();

        Assert.Equal(expected, loaded.SidebarWidth);
    }

    [Theory]
    [InlineData(0.01, LayoutSettings.MinFocusRailWeight)]
    [InlineData(5, LayoutSettings.MaxFocusRailWeight)]
    public async Task SaveAsync_ClampsAnOutOfRangeFocusRailWeight(double requested, double expected)
    {
        var store = new LayoutSettingsStore(_configFilePath);

        await store.SaveAsync(new LayoutSettings { FocusRailWeight = requested });
        var loaded = await store.LoadAsync();

        Assert.Equal(expected, loaded.FocusRailWeight);
    }

    [Fact]
    public async Task LoadAsync_ClampsAStaleOutOfRangeValueFromDisk()
    {
        // Simulates a hand-edited (or pre-#49) cockpit.json holding a value outside today's min/max,
        // written directly rather than through the store — SaveAsync would already clamp it itself.
        await File.WriteAllTextAsync(_configFilePath, """{ "Layout": { "SidebarWidth": 9001 } }""");
        var store = new LayoutSettingsStore(_configFilePath);

        var loaded = await store.LoadAsync();

        Assert.Equal(LayoutSettings.MaxSidebarWidth, loaded.SidebarWidth);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var behaviorStore = new SessionBehaviorSettingsStore(_configFilePath);
        await behaviorStore.SaveAsync(new SessionBehaviorSettings { AutoCloseOnExit = true });

        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        Assert.Equal("https://example/webhook", (await notificationStore.LoadAsync()).WebhookUrl);
        Assert.True((await behaviorStore.LoadAsync()).AutoCloseOnExit);
        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
