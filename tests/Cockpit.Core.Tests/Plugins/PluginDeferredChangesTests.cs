using System.IO.Compression;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// What a restart is for (AC-455). An update over an existing install and a removal are both deferred to the
/// next start, because that is the one moment no plugin assembly is loaded. Discovery used to apply them on its
/// way past — and discovery runs after every enable/disable/remove and on the update checker's fifteen-minute
/// timer, so the deferral was decided by whoever happened to rediscover first.
/// </summary>
public class PluginDeferredChangesTests : IDisposable
{
    private const int HostMajor = 1;

    /// <summary>
    /// The one that made the deferral a fiction. A staged update sits beside the folder it replaces until a
    /// restart; a rediscovery is a read and has to leave both exactly as it found them.
    /// </summary>
    [Fact]
    public async Task ARediscovery_LeavesAStagedUpdateWhereItIs()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);

        await _bootstrap.DiscoverAsync(HostMajor);

        Assert.Equal("MZ-v1", await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll")));
        Assert.True(
            Directory.Exists(Path.Combine(_pluginsRoot, ".pending-updates", "acme")),
            "the staged copy is still waiting for the restart that may apply it");
    }

    /// <summary>
    /// And the same for a removal — but only on disk. The folder waits for the restart that deletes it, while
    /// the plugin drops out of what discovery reports at once. That is what the operator was told ("it will be
    /// uninstalled on the next restart") and what they see: it leaves the Installed list when they press Remove.
    /// The deleting used to do both jobs; only one of them belonged to it.
    /// </summary>
    [Fact]
    public async Task ARediscovery_LeavesAMarkedRemovalOnDisk_ButStopsReportingIt()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        Assert.Single(await _bootstrap.DiscoverAsync(HostMajor));

        await _installer.MarkForRemovalAsync("acme");

        Assert.Empty(await _bootstrap.DiscoverAsync(HostMajor));
        Assert.True(Directory.Exists(Path.Combine(_pluginsRoot, "acme")), "the folder is the restart's to delete");
    }

    /// <summary>
    /// The same skip is what stops a removal the sweep could not carry out from coming back to life. Deleting a
    /// folder is best-effort — a locked file leaves the marker for the next start — and before this that plugin
    /// was rediscovered and loaded on every start after it, forever.
    /// </summary>
    [Fact]
    public async Task APluginWhoseDeletionFailed_IsStillNotReported()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.MarkForRemovalAsync("acme");

        // What a failed Directory.Delete leaves behind: the marker, and the folder it is in.
        await _bootstrap.ApplyPendingChangesAndDiscoverAsync(HostMajor);
        Directory.CreateDirectory(Path.Combine(_pluginsRoot, "acme"));
        await File.WriteAllTextAsync(Path.Combine(_pluginsRoot, "acme", PluginInstaller.RemovalMarker), "");
        await File.WriteAllTextAsync(Path.Combine(_pluginsRoot, "acme", "plugin.json"), _Manifest("acme"));
        await File.WriteAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll"), "MZ-v1");

        Assert.Empty(await _bootstrap.DiscoverAsync(HostMajor));
    }

    /// <summary>
    /// The startup pass is what applies both, and it is the only thing that does. Without this the two above
    /// pass on a bootstrap that has simply stopped sweeping anywhere at all.
    /// </summary>
    [Fact]
    public async Task TheStartupPass_AppliesBothOfThem()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);
        await _installer.InstallFromZipAsync(_PluginZip("gone", dll: "MZ"), HostMajor);
        await _installer.MarkForRemovalAsync("gone");

        await _bootstrap.ApplyPendingChangesAndDiscoverAsync(HostMajor);

        Assert.Equal("MZ-v2", await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll")));
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "gone")));
    }

    /// <summary>
    /// Update a plugin, then decide to remove it, and the removal used to be the one that lost: the startup
    /// sweep applied the staged update first, which deletes the folder — and the removal marker inside it went
    /// with it. The plugin came back, at a version the operator had just decided not to keep, saying nothing.
    /// </summary>
    [Fact]
    public async Task Removing_APluginWithAStagedUpdate_DoesNotBringItBack()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);

        await _installer.MarkForRemovalAsync("acme");
        await _bootstrap.ApplyPendingChangesAndDiscoverAsync(HostMajor);

        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, "acme")), "the operator removed it");
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, ".pending-updates", "acme")));
    }

    /// <summary>
    /// The other order is the operator changing their mind back, and the install has to win there. It already
    /// did — the swap deletes the marker along with the folder — and this is here so the fix above cannot be
    /// widened into "a removal always wins" without the reversal being noticed. It pins the outcome, not the
    /// order the two sweeps run in: withdrawing the staged copy at the moment of removal is what makes the two
    /// intentions unable to coexist, so neither sweep has to run first any more.
    /// </summary>
    [Fact]
    public async Task Reinstalling_APluginMarkedForRemoval_KeepsIt()
    {
        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v1"), HostMajor);
        await _installer.MarkForRemovalAsync("acme");

        await _installer.InstallFromZipAsync(_PluginZip("acme", dll: "MZ-v2"), HostMajor);
        await _bootstrap.ApplyPendingChangesAndDiscoverAsync(HostMajor);

        Assert.Equal("MZ-v2", await File.ReadAllTextAsync(Path.Combine(_pluginsRoot, "acme", "Plugin.dll")));
    }

    private readonly string _tempDir;
    private readonly string _pluginsRoot;
    private readonly PluginInstaller _installer;
    private readonly PluginBootstrap _bootstrap;

    public PluginDeferredChangesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-plugin-deferred-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _pluginsRoot = Path.Combine(_tempDir, "plugins");
        _installer = new PluginInstaller(_pluginsRoot);
        // A config file that does not exist yet: the registration store reads it and finds nothing, which is
        // what a plugin nobody has approved looks like. Pointed at the temp dir so no test reads the developer's.
        _bootstrap = new PluginBootstrap(_pluginsRoot, Path.Combine(_tempDir, "cockpit.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string _PluginZip(string id, string dll)
    {
        var zipPath = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        _Write(archive, "plugin.json", _Manifest(id));
        _Write(archive, "Plugin.dll", dll);

        return zipPath;
    }

    private static string _Manifest(string id) =>
        $$"""{"id":"{{id}}","name":"{{id}}","version":"1.0.0","entryAssembly":"Plugin.dll","abstractionsVersion":{{HostMajor}}}""";

    private static void _Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
}
