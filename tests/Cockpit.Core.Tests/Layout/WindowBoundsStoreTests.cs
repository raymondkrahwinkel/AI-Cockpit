using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Layout;

/// <summary>
/// Load/save round-trip for the window-bounds section of <c>cockpit.json</c>, the null-when-unset case, and
/// the shared-file invariant that saving bounds leaves a sibling section intact.
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

        (await store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTrips()
    {
        var store = new WindowBoundsStore(_configFilePath);
        var bounds = new WindowBounds(120, 80, 1400, 900, IsMaximized: true);

        await store.SaveAsync(bounds);

        (await store.LoadAsync()).Should().Be(bounds);
    }

    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsIntact()
    {
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var store = new WindowBoundsStore(_configFilePath);
        await store.SaveAsync(new WindowBounds(0, 0, 1280, 820, IsMaximized: false));

        (await notificationStore.LoadAsync()).WebhookUrl.Should().Be("https://example/webhook");
        (await store.LoadAsync())!.Width.Should().Be(1280);
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(400, true)]
    [InlineData(1280, true)]
    public void HasUsableSize_GuardsAgainstDegenerateSizes(int size, bool expected)
        => new WindowBounds(0, 0, size, size, false).HasUsableSize.Should().Be(expected);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
