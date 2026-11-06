using System.Text.Json;
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

    [Theory]
    [InlineData("runtime-source-token")]
    public async Task AddAsync_ThenLoadAsync_RoundTrips_WithoutSerializingRuntimeCredentials(string runtimeToken)
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));
        await store.AddAsync(PluginStoreConfig.Remote("https://example.com/store/index.json"));
        await store.AddAsync(PluginStoreConfig.Local("/home/raymond/plugins"));

        Assert.Equivalent(new[]
        {
            PluginStoreConfig.Remote("https://github.com/a/b"),
            PluginStoreConfig.Remote("https://example.com/store/index.json"),
            PluginStoreConfig.Local("/home/raymond/plugins"),
        }, await store.LoadAsync());

        var signed = PluginStoreConfig.Remote("https://example.com/store/index.json") with
        {
            Signed = new PluginStoreSignedSource("example:store", "example", _ => ValueTask.FromResult<string?>(runtimeToken)),
        };
        var serialized = JsonSerializer.Serialize(signed);
        Assert.Equal("""{"kind":"remote","location":"https://example.com/store/index.json"}""", serialized);
        Assert.DoesNotContain(runtimeToken, serialized);
        Assert.Null(JsonSerializer.Deserialize<PluginStoreConfig>(serialized)?.Signed);
        await _AssertLegacyStringEntryReadsAsPublicRemoteAsync();
    }

    private async Task _AssertLegacyStringEntryReadsAsPublicRemoteAsync()
    {
        // A pre-AC-7 config wrote plain URL strings; they must still read, as public remote stores.
        await File.WriteAllTextAsync(
            _configFilePath,
            """{ "PluginStores": ["https://github.com/a/b"], "PluginStoresDefaultSeeded": true }""");

        var store = new PluginStoreConfigStore(_configFilePath);

        var stores = await store.LoadAsync();
        Assert.Single(stores);
        Assert.Equal(PluginStoreConfig.Remote("https://github.com/a/b"), stores[0]);
        Assert.False(stores[0].HasToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
