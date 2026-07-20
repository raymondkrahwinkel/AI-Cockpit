using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Clones;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

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
    public async Task LoadAsync_WithNothingSaved_ReturnsNoOverride()
    {
        (await _store.LoadAsync()).Root.Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheOverrideRoot()
    {
        var custom = Path.Combine(_tempRoot, "somewhere-else");

        await _store.SaveAsync(new CloneSettings { Root = custom });

        (await _store.LoadAsync()).Root.Should().Be(custom);
    }

    [Fact]
    public async Task SaveNullRoot_ClearsTheOverride()
    {
        await _store.SaveAsync(new CloneSettings { Root = Path.Combine(_tempRoot, "x") });
        await _store.SaveAsync(new CloneSettings { Root = null });

        (await _store.LoadAsync()).Root.Should().BeNull();
    }

    [Fact]
    public void DefaultRoot_IsTheManagedClonesRoot()
    {
        _store.DefaultRoot.Should().Be(CockpitConfigPath.ClonesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
