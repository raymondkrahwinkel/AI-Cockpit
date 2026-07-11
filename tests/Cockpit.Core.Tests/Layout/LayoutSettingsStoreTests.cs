using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.SessionBehavior;
using FluentAssertions;

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

        settings.SingleSessionLayout.Should().BeFalse();
        settings.SidebarWidth.Should().Be(LayoutSettings.DefaultSidebarWidth);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new LayoutSettingsStore(_configFilePath);

        await store.SaveAsync(new LayoutSettings { SingleSessionLayout = true, StackSessionsVertically = true, MinimizeToTrayOnClose = true, SidebarWidth = 260 });
        var loaded = await store.LoadAsync();

        loaded.SingleSessionLayout.Should().BeTrue();
        loaded.StackSessionsVertically.Should().BeTrue();
        loaded.MinimizeToTrayOnClose.Should().BeTrue();
        loaded.SidebarWidth.Should().Be(260);
    }

    [Theory]
    [InlineData(50, LayoutSettings.MinSidebarWidth)]
    [InlineData(900, LayoutSettings.MaxSidebarWidth)]
    public async Task SaveAsync_ClampsAnOutOfRangeSidebarWidth(double requested, double expected)
    {
        var store = new LayoutSettingsStore(_configFilePath);

        await store.SaveAsync(new LayoutSettings { SidebarWidth = requested });
        var loaded = await store.LoadAsync();

        loaded.SidebarWidth.Should().Be(expected);
    }

    [Fact]
    public async Task LoadAsync_ClampsAStaleOutOfRangeValueFromDisk()
    {
        // Simulates a hand-edited (or pre-#49) cockpit.json holding a value outside today's min/max,
        // written directly rather than through the store — SaveAsync would already clamp it itself.
        await File.WriteAllTextAsync(_configFilePath, """{ "Layout": { "SidebarWidth": 9001 } }""");
        var store = new LayoutSettingsStore(_configFilePath);

        var loaded = await store.LoadAsync();

        loaded.SidebarWidth.Should().Be(LayoutSettings.MaxSidebarWidth);
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

        (await notificationStore.LoadAsync()).WebhookUrl.Should().Be("https://example/webhook");
        (await behaviorStore.LoadAsync()).AutoCloseOnExit.Should().BeTrue();
        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
