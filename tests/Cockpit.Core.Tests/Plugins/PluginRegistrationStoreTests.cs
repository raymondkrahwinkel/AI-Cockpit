using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Layout;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Plugins;

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

        Assert.Empty((await store.LoadAllAsync()));
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAllAsync_RoundTrips()
    {
        var store = new PluginRegistrationStore(_configFilePath);

        await store.SaveAsync("github-issues", new PluginRegistration(Enabled: true, PinnedSha256: "abc123"));
        await store.SaveAsync("weather", new PluginRegistration(Enabled: false, PinnedSha256: "def456"));

        var loaded = await store.LoadAllAsync();
        Assert.Equal(2, System.Linq.Enumerable.Count(loaded));
        Assert.Equal(new PluginRegistration(true, "abc123"), loaded["github-issues"]);
        Assert.Equal(new PluginRegistration(false, "def456"), loaded["weather"]);
    }

    [Fact]
    public async Task RemoveAsync_DropsOnlyThatPlugin()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("a", new PluginRegistration(true, "h1"));
        await store.SaveAsync("b", new PluginRegistration(true, "h2"));

        await store.RemoveAsync("a");

        var loaded = await store.LoadAllAsync();
        Assert.Contains("b", loaded.Keys);
        Assert.DoesNotContain("a", loaded.Keys);
    }

    [Fact]
    public async Task SaveAsync_LeavesOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var pluginStore = new PluginRegistrationStore(_configFilePath);
        await pluginStore.SaveAsync("x", new PluginRegistration(true, "h"));

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        var loaded = await pluginStore.LoadAllAsync();
        Assert.Contains("x", loaded.Keys);
    }

    [Fact]
    public async Task SaveDataAsync_ThenLoadDataAsync_RoundTrips()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        var data = new Dictionary<string, string> { ["token"] = "\"secret\"", ["repo"] = "\"owner/name\"" };

        await store.SaveDataAsync("github-issues", data);

        Assert.Equivalent(data, (await store.LoadDataAsync("github-issues")));
    }

    [Fact]
    public async Task SaveDataAsync_PreservesEnabledAndHash()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveAsync("p", new PluginRegistration(Enabled: true, PinnedSha256: "hash-1"));

        await store.SaveDataAsync("p", new Dictionary<string, string> { ["k"] = "\"v\"" });

        Assert.Equal(new PluginRegistration(true, "hash-1"), (await store.LoadAllAsync())["p"]);
    }

    [Fact]
    public async Task SaveAsync_PreservesStoredData()
    {
        var store = new PluginRegistrationStore(_configFilePath);
        await store.SaveDataAsync("p", new Dictionary<string, string> { ["k"] = "\"v\"" });

        await store.SaveAsync("p", new PluginRegistration(Enabled: false, PinnedSha256: "hash-2"));

        var data = await store.LoadDataAsync("p");
        Assert.Contains("k", data.Keys);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
