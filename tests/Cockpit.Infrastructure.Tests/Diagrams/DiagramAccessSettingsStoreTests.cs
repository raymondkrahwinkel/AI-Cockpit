using Cockpit.Core.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>The diagram-access master switch persists across restarts, and a config that never saved it defaults to off (AC-810).</summary>
public class DiagramAccessSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"diagram-access-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_WhenNothingSaved_DefaultsToOff()
    {
        var store = new DiagramAccessSettingsStore(_path);

        Assert.False((await store.LoadAsync()).Enabled);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new DiagramAccessSettingsStore(_path);

        await store.SaveAsync(new DiagramAccessSettings { Enabled = true });

        Assert.True((await new DiagramAccessSettingsStore(_path).LoadAsync()).Enabled);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
