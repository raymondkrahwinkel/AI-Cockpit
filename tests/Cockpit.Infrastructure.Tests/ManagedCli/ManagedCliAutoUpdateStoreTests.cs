using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Infrastructure.Terminal;

namespace Cockpit.Infrastructure.Tests.ManagedCli;

/// <summary>Auto-update (AC-767) is on for every CLI a config never mentioned, and turning it off persists per CLI without disturbing a sibling config section.</summary>
public class ManagedCliAutoUpdateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"managed-cli-auto-update-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task IsEnabledAsync_WhenNothingSaved_DefaultsToTrue()
    {
        var store = new ManagedCliAutoUpdateStore(_path);

        Assert.True(await store.IsEnabledAsync("claude"));
    }

    [Fact]
    public async Task SetFalse_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new ManagedCliAutoUpdateStore(_path);

        await store.SetAsync("claude", enabled: false);

        Assert.False(await new ManagedCliAutoUpdateStore(_path).IsEnabledAsync("claude"));
    }

    [Fact]
    public async Task SetFalse_ForOneCli_LeavesAnotherCliEnabled()
    {
        var store = new ManagedCliAutoUpdateStore(_path);

        await store.SetAsync("claude", enabled: false);

        Assert.True(await store.IsEnabledAsync("codex"));
    }

    [Fact]
    public async Task SetTrue_AfterSetFalse_RemovesTheException()
    {
        var store = new ManagedCliAutoUpdateStore(_path);
        await store.SetAsync("claude", enabled: false);

        await store.SetAsync("claude", enabled: true);

        Assert.True(await store.IsEnabledAsync("claude"));
    }

    [Fact]
    public async Task Save_LeavesASiblingConfigSectionIntact()
    {
        await new TerminalAccessSettingsStore(_path).SaveAsync(new TerminalAccessSettings { Enabled = true });

        await new ManagedCliAutoUpdateStore(_path).SetAsync("claude", enabled: false);

        Assert.True((await new TerminalAccessSettingsStore(_path).LoadAsync()).Enabled);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
