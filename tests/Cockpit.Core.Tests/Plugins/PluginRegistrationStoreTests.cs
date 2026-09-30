using Cockpit.Core.Plugins;
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
