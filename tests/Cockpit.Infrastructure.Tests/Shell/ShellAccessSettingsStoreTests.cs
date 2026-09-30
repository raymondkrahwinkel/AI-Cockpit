using Cockpit.Core.Shell;
using Cockpit.Infrastructure.Shell;

namespace Cockpit.Infrastructure.Tests.Shell;

/// <summary>The shell-access master switch persists across restarts, and a config that never saved it defaults to off (AC-1066).</summary>
public class ShellAccessSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"shell-access-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_WhenNothingSaved_DefaultsToOff()
    {
        var store = new ShellAccessSettingsStore(_path);

        Assert.False((await store.LoadAsync()).Enabled);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new ShellAccessSettingsStore(_path);

        await store.SaveAsync(new ShellAccessSettings { Enabled = true });

        Assert.True((await new ShellAccessSettingsStore(_path).LoadAsync()).Enabled);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
