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

    [Fact]
    public async Task LoadAsync_FreshInstall_SeedsDefaultStoreAndMarksSeeded()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        var stores = await store.LoadAsync();

        stores.Should().BeEquivalentTo(PluginStoreConfigStore.DefaultStoreUrl);

        // Persisted, not just an in-memory return value — round-trips on a fresh instance too.
        var reloaded = new PluginStoreConfigStore(_configFilePath);
        (await reloaded.LoadAsync()).Should().BeEquivalentTo(PluginStoreConfigStore.DefaultStoreUrl);
    }

    [Fact]
    public async Task LoadAsync_AlreadySeededAndEmptied_DoesNotReAddDefault()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.LoadAsync(); // seeds the default + sets the marker

        await store.RemoveAsync(PluginStoreConfigStore.DefaultStoreUrl);

        (await store.LoadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_PreExistingInstallWithOwnStores_LeavesListUntouchedButMarksSeeded()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        // Simulates a pre-#43 install: stores already configured, marker never set.
        await store.AddAsync("https://github.com/a/b");

        var stores = await store.LoadAsync();

        stores.Should().BeEquivalentTo("https://github.com/a/b");

        // Marker is now set, so emptying the list afterwards must not seed the default either.
        await store.RemoveAsync("https://github.com/a/b");
        (await store.LoadAsync()).Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
