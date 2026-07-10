using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Add/load/remove for the <c>pluginStores</c> section of <c>cockpit.json</c> (#14), idempotent add and sibling-section-intact.</summary>
public class PluginStoreConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public PluginStoreConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-plugin-store-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task AddAsync_ThenLoadAsync_RoundTrips()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync("https://github.com/a/b");
        await store.AddAsync("https://example.com/store/index.json");

        (await store.LoadAsync()).Should().BeEquivalentTo("https://github.com/a/b", "https://example.com/store/index.json");
    }

    [Fact]
    public async Task AddAsync_Duplicate_IsIdempotent()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync("https://github.com/a/b");
        await store.AddAsync("https://GitHub.com/a/b".Replace("GitHub", "github"));
        await store.AddAsync("https://github.com/a/b");

        (await store.LoadAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyThatStore()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.AddAsync("https://one");
        await store.AddAsync("https://two");

        await store.RemoveAsync("https://one");

        (await store.LoadAsync()).Should().BeEquivalentTo("https://two");
    }

    [Fact]
    public async Task AddAsync_LeavesOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var storeConfig = new PluginStoreConfigStore(_configFilePath);
        await storeConfig.AddAsync("https://github.com/a/b");

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await storeConfig.LoadAsync()).Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
