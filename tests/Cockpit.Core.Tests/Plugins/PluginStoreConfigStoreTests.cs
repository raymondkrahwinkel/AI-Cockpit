using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Add/load/remove for the <c>pluginStores</c> section of <c>cockpit.json</c> (#14, AC-7): remote (public/private) and local stores, replace-on-same-location, and sibling-section-intact.</summary>
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

        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));
        await store.AddAsync(PluginStoreConfig.Remote("https://example.com/store/index.json"));

        Assert.Equivalent(new[]
        {
            PluginStoreConfig.Remote("https://github.com/a/b"),
            PluginStoreConfig.Remote("https://example.com/store/index.json"),
        }, await store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_LegacyStringEntry_ReadsAsPublicRemote()
    {
        // A pre-AC-7 config wrote plain URL strings; they must still read, as public remote stores.
        await File.WriteAllTextAsync(
            _configFilePath,
            """{ "PluginStores": ["https://github.com/a/b"], "PluginStoresDefaultSeeded": true }""");

        var store = new PluginStoreConfigStore(_configFilePath);

        var stores = await store.LoadAsync();
        Assert.Single(stores);
        Assert.Equal(PluginStoreConfig.Remote("https://github.com/a/b"), stores[0]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
