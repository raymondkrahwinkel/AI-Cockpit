using Cockpit.Infrastructure.ManagedCli;

namespace Cockpit.Infrastructure.Tests.ManagedCli;

/// <summary>Auto-update (AC-767) is on for every CLI a config never mentioned, and turning it off persists per CLI without disturbing a sibling config section.</summary>
public class ManagedCliAutoUpdateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"managed-cli-auto-update-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task SetFalse_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new ManagedCliAutoUpdateStore(_path);

        await store.SetAsync("claude", enabled: false);

        Assert.False(await new ManagedCliAutoUpdateStore(_path).IsEnabledAsync("claude"));
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
