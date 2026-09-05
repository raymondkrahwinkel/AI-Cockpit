using Cockpit.Core.Depot;
using Cockpit.Infrastructure.Depot;

namespace Cockpit.Infrastructure.Tests.Depot;

/// <summary>
/// One counter-example per AC-279 acceptance criterion: a stable, safe-on-disk mirror path (1), registry and
/// root-override surviving a restart with an existing item keeping its absolute path (2), reconcile forgetting
/// only vanished mirrors while never deleting a folder itself (3), and disable/remove retaining local work with a
/// notice while an empty mirror is simply dropped (4).
/// </summary>
public sealed class DepotMirrorManagerTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-depotmirror-{Guid.NewGuid():n}");

    [Fact]
    public async Task EnsureAsync_DerivesAStableFolderUnderMirrorsRoot_AndKeepsUnsafeIdsSafeOnDisk()
    {
        var mirrorsRoot = Path.Combine(_tempRoot, "depot-mirrors");
        var manager = new DepotMirrorManager(new DepotMirrorRegistryStore(Path.Combine(_tempRoot, "cockpit.json")), mirrorsRoot);
        const string unsafeHost = "depot.example.com";
        const string unsafeSlug = "Ünsafe / slug?ø";

        var record = await manager.EnsureAsync(unsafeHost, unsafeSlug);

        Assert.StartsWith(Path.GetFullPath(mirrorsRoot), record.Path);
        Assert.True(Directory.Exists(record.Path));
        Assert.DoesNotContain('?', record.Path);
        Assert.DoesNotContain(' ', record.Path);

        // Stable: deriving the same path again from the same raw ids yields byte-for-byte the same folder.
        var root = await manager.GetEffectiveMirrorsRootAsync();
        Assert.Equal(record.Path, manager.BuildMirrorPath(root, unsafeHost, unsafeSlug));
    }

    [Fact]
    public async Task RegistryAndRootOverride_SurviveARestart_AndAnExistingItemKeepsItsAbsolutePath()
    {
        var configPath = Path.Combine(_tempRoot, "cockpit.json");
        var settings = new DepotMirrorSettingsStore(configPath);

        // An override is set before the first resolve so this test never falls through to the real, production
        // mirrors root under the operator's actual app state directory.
        var initialRoot = Path.Combine(_tempRoot, "depot-mirrors");
        await settings.SaveAsync(new DepotMirrorSettings { Root = initialRoot });

        var firstRun = new DepotMirrorManager(new DepotMirrorRegistryStore(configPath), settings);
        var created = await firstRun.EnsureAsync("depot.example.com", "cockpit");

        // The operator moves the mirrors root after the mirror was created.
        var overrideRoot = Path.Combine(_tempRoot, "custom-mirrors");
        await settings.SaveAsync(new DepotMirrorSettings { Root = overrideRoot });

        // A fresh set of stores against the same file simulates a restart.
        var restarted = new DepotMirrorManager(new DepotMirrorRegistryStore(configPath), new DepotMirrorSettingsStore(configPath));

        Assert.Equal(Path.GetFullPath(overrideRoot), await restarted.GetEffectiveMirrorsRootAsync());

        var reEnsured = await restarted.EnsureAsync("depot.example.com", "cockpit");
        Assert.Equal(created.Path, reEnsured.Path);
        Assert.StartsWith(Path.GetFullPath(initialRoot), reEnsured.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconcileAsync_ForgetsAVanishedMirror_ButKeepsOneStillOnDisk()
    {
        var registry = new DepotMirrorRegistryStore(Path.Combine(_tempRoot, "cockpit.json"));
        var manager = new DepotMirrorManager(registry, Path.Combine(_tempRoot, "depot-mirrors"));
        var present = await manager.EnsureAsync("depot.example.com", "cockpit");
        await registry.AddAsync(new DepotMirror(
            "depot.example.com", "gone", Path.Combine(_tempRoot, "depot-mirrors", "depot.example.com", "gone"), DateTimeOffset.UtcNow));

        await manager.ReconcileAsync();

        var remaining = await registry.ListAsync();
        Assert.Equal(present.Path, Assert.Single(remaining).Path);
        Assert.True(Directory.Exists(present.Path));
    }

    [Fact]
    public async Task RemoveAsync_RetainsAMirrorWithLocalWorkAndNotifies_ButDropsAnEmptyOne()
    {
        var registry = new DepotMirrorRegistryStore(Path.Combine(_tempRoot, "cockpit.json"));
        var manager = new DepotMirrorManager(registry, Path.Combine(_tempRoot, "depot-mirrors"));
        var withWork = await manager.EnsureAsync("depot.example.com", "has-local-work");
        File.WriteAllText(Path.Combine(withWork.Path, "unsynced.txt"), "not synced yet");
        var empty = await manager.EnsureAsync("depot.example.com", "empty");

        var noticeForWork = await manager.RemoveAsync(withWork);
        var noticeForEmpty = await manager.RemoveAsync(empty);

        Assert.NotNull(noticeForWork);
        Assert.True(Directory.Exists(withWork.Path));
        Assert.True(File.Exists(Path.Combine(withWork.Path, "unsynced.txt")));
        var retained = Assert.Single(await registry.ListAsync());
        Assert.Equal(withWork.Path, retained.Path);
        Assert.True(retained.IsRetained);

        Assert.Null(noticeForEmpty);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
