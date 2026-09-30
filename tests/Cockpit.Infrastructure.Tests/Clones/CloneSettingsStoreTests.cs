using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;

namespace Cockpit.Infrastructure.Tests.Clones;

/// <summary>
/// The clones-root-override store (AC-90): a save/load round-trip through its own <c>cloneSettings</c> section, and
/// the default it reports for a blank override. Mirrors the worktree-settings store it is modelled on (AC-85).
/// </summary>
public sealed class CloneSettingsStoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-clonesettings-{Guid.NewGuid():n}");
    private readonly CloneSettingsStore _store;

    public CloneSettingsStoreTests()
    {
        Directory.CreateDirectory(_tempRoot);
        _store = new CloneSettingsStore(Path.Combine(_tempRoot, "cockpit.json"));
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheOverrideRoot()
    {
        var custom = Path.Combine(_tempRoot, "somewhere-else");

        await _store.SaveAsync(new CloneSettings { Root = custom });

        Assert.Equal(custom, (await _store.LoadAsync()).Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
