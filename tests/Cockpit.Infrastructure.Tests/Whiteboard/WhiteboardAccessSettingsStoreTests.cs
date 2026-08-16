using Cockpit.Core.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;

namespace Cockpit.Infrastructure.Tests.Whiteboard;

/// <summary>The whiteboard-access master switch persists across restarts, and a config that never saved it defaults to off (AC-823).</summary>
public class WhiteboardAccessSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"whiteboard-access-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_WhenNothingSaved_DefaultsToOff()
    {
        var store = new WhiteboardAccessSettingsStore(_path);

        Assert.False((await store.LoadAsync()).Enabled);
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new WhiteboardAccessSettingsStore(_path);

        await store.SaveAsync(new WhiteboardAccessSettings { Enabled = true });

        Assert.True((await new WhiteboardAccessSettingsStore(_path).LoadAsync()).Enabled);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
