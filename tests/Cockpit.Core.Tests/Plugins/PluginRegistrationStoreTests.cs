using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Load/save/remove round-trip for the <c>plugins</c> section of <c>cockpit.json</c> (#14), plus the sibling-section-intact invariant.</summary>
public class PluginRegistrationStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public PluginRegistrationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-plugin-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAllAsync_NoConfigFile_ReturnsEmpty()
    {
        var store = new PluginRegistrationStore(_configFilePath);

        (await store.LoadAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAllAsync_RoundTrips()
    {
        var store = new PluginRegistrationStore(_configFilePath);

        await store.SaveAsync("github-issues", new PluginRegistration(Enabled: true, PinnedSha256: "abc123"));
        await store.SaveAsync("weather", new PluginRegistration(Enabled: false, PinnedSha256: "def456"));

        var loaded = await store.LoadAllAsync();
        loaded.Should().HaveCount(2);
        loaded["github-issues"].Should().Be(new PluginRegistration(true, "abc123"));
        loaded["weather"].Should().Be(new PluginRegistration(false, "def456"));
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyThatPlugin()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("a", new PluginRegistration(true, "h1"));
        await store.SaveAsync("b", new PluginRegistration(true, "h2"));

        await store.RemoveAsync("a");

        var loaded = await store.LoadAllAsync();
        loaded.Should().ContainKey("b");
        loaded.Should().NotContainKey("a");
    }

    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var pluginStore = new PluginRegistrationStore(_configFilePath);
        await pluginStore.SaveAsync("x", new PluginRegistration(true, "h"));

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await pluginStore.LoadAllAsync()).Should().ContainKey("x");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
