using Cockpit.Core.Layout;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Plugins;
using FluentAssertions;

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

        (await store.LoadAsync()).Should().BeEquivalentTo(new[]
        {
            PluginStoreConfig.Remote("https://github.com/a/b"),
            PluginStoreConfig.Remote("https://example.com/store/index.json"),
        });
    }

    [Fact]
    public async Task AddAsync_SameLocation_ReplacesRatherThanDuplicating()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));
        await store.AddAsync(PluginStoreConfig.Remote("https://GitHub.com/a/b".Replace("GitHub", "github")));
        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));

        (await store.LoadAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task AddAsync_SameLocationWithNewToken_UpdatesTheToken()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b", "old-token"));
        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b", "new-token"));

        var stores = await store.LoadAsync();
        stores.Should().ContainSingle();
        stores[0].Token.Should().Be("new-token");
    }

    [Fact]
    public async Task AddAsync_LocalStore_RoundTripsKindAndPath()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        await store.AddAsync(PluginStoreConfig.Local("/home/raymond/my-plugins"));

        var stores = await store.LoadAsync();
        stores.Should().ContainSingle();
        stores[0].Kind.Should().Be(PluginStoreKind.Local);
        stores[0].Location.Should().Be("/home/raymond/my-plugins");
    }

    [Fact]
    public async Task AddAsync_LocalStoresDifferingOnlyByCase_AreDistinct()
    {
        // Filesystem paths are case-sensitive on Linux: two folders differing only by case are two stores, and
        // adding one must not silently replace the other.
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.AddAsync(PluginStoreConfig.Local("/home/me/Plugins"));
        await store.AddAsync(PluginStoreConfig.Local("/home/me/plugins"));

        (await store.LoadAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyThatStore()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.AddAsync(PluginStoreConfig.Remote("https://one"));
        await store.AddAsync(PluginStoreConfig.Remote("https://two"));

        await store.RemoveAsync(PluginStoreConfig.Remote("https://one"));

        (await store.LoadAsync()).Should().BeEquivalentTo(new[] { PluginStoreConfig.Remote("https://two") });
    }

    [Fact]
    public async Task RemoveAsync_MatchesOnLocationRegardlessOfToken()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.LoadAsync(); // seeds the default + sets the marker, so the final load does not re-seed
        await store.AddAsync(PluginStoreConfig.Remote("https://one", "a-token"));

        // The remove request carries no token, but it is the same store.
        await store.RemoveAsync(PluginStoreConfig.Remote("https://one"));

        (await store.LoadAsync()).Should().NotContain(existing => existing.Location == "https://one");
    }

    [Fact]
    public async Task AddAsync_LeavesOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var storeConfig = new PluginStoreConfigStore(_configFilePath);
        await storeConfig.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await storeConfig.LoadAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task LoadAsync_FreshInstall_SeedsDefaultStoreAndMarksSeeded()
    {
        var store = new PluginStoreConfigStore(_configFilePath);

        var stores = await store.LoadAsync();

        stores.Should().BeEquivalentTo(new[] { PluginStoreConfig.Remote(PluginStoreConfigStore.DefaultStoreUrl) });

        // Persisted, not just an in-memory return value — round-trips on a fresh instance too.
        var reloaded = new PluginStoreConfigStore(_configFilePath);
        (await reloaded.LoadAsync()).Should().BeEquivalentTo(new[] { PluginStoreConfig.Remote(PluginStoreConfigStore.DefaultStoreUrl) });
    }

    [Fact]
    public async Task LoadAsync_AlreadySeededAndEmptied_DoesNotReAddDefault()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        await store.LoadAsync(); // seeds the default + sets the marker

        await store.RemoveAsync(PluginStoreConfig.Remote(PluginStoreConfigStore.DefaultStoreUrl));

        (await store.LoadAsync()).Should().BeEmpty();
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
        stores.Should().ContainSingle();
        stores[0].Should().Be(PluginStoreConfig.Remote("https://github.com/a/b"));
    }

    [Fact]
    public async Task LoadAsync_PreExistingInstallWithOwnStores_LeavesListUntouchedButMarksSeeded()
    {
        var store = new PluginStoreConfigStore(_configFilePath);
        // Simulates a pre-#43 install: stores already configured, marker never set.
        await store.AddAsync(PluginStoreConfig.Remote("https://github.com/a/b"));

        var stores = await store.LoadAsync();

        stores.Should().BeEquivalentTo(new[] { PluginStoreConfig.Remote("https://github.com/a/b") });

        // Marker is now set, so emptying the list afterwards must not seed the default either.
        await store.RemoveAsync(PluginStoreConfig.Remote("https://github.com/a/b"));
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
