using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Terminal;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Terminal;

/// <summary>The terminal-access master switch persists across restarts, and a config that never saved it defaults to off (AC-34).</summary>
public class TerminalAccessSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"terminal-access-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Load_WhenNothingSaved_DefaultsToOff()
    {
        var store = new TerminalAccessSettingsStore(_path);

        (await store.LoadAsync()).Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTripsTheSwitch()
    {
        var store = new TerminalAccessSettingsStore(_path);

        await store.SaveAsync(new TerminalAccessSettings { Enabled = true });

        (await new TerminalAccessSettingsStore(_path).LoadAsync()).Enabled.Should().BeTrue();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }
    }
}
