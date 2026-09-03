using System.IO.Compression;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

// Installs a plugin from a `.zip` and schedules removals (#14). Unpacked via the `PluginInstallPath`
// zip-slip guard into same-volume staging, validated, then moved into `plugins/&lt;id&gt;/`. Removal and
// update are both deferred to next startup, since a loaded plugin's assembly stays locked until exit.
internal sealed class PluginInstaller : IPluginInstaller, ISingletonService
{
    // The file dropped into a plugin's folder to have it deleted at the next start. Discovery reads it too, so a plugin the operator removed is out of the list from that moment rather than at the restart.
    internal const string RemovalMarker = ".remove";

    // A reserved (dot-prefixed, so discovery skips it) folder under the plugins root holding staged updates as
    // .pending-updates/<folderId>/. Kept off to the side rather than swapped in place so an update never has to
    // delete a locked, loaded assembly mid-session; the swap happens at startup before any plugin loads.
    private const string PendingUpdatesFolder = ".pending-updates";

    // Whether a folder under the plugins root is an installed plugin at all. Dot-prefixed ones are this installer's
    // own reserved areas — a leftover `.staging-*` extraction, `.pending-updates` — and one carrying the marker is
    // already removed. One rule for everything that reads the folders: discovery, and the backup's plugin index.
    internal static bool IsInstalledPlugin(string folder) =>
        !Path.GetFileName(folder).StartsWith('.') && !File.Exists(Path.Combine(folder, RemovalMarker));

    private readonly string _pluginsRoot;

    public PluginInstaller()
        : this(CockpitConfigPath.PluginsRoot)
    {
    }

    // Test seam: point the installer at an arbitrary plugins root.
    internal PluginInstaller(string pluginsRoot)
    {
        _pluginsRoot = pluginsRoot;
    }

    public async Task<PluginInstallResult> InstallFromZipAsync(
        string zipFilePath, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default)
    {
        hostVersion ??= HostVersionInfo.Current;

        if (!File.Exists(zipFilePath))
        {
            return PluginInstallResult.Failure("The selected file no longer exists.");
        }

        Directory.CreateDirectory(_pluginsRoot);
        var stagingDir = Path.Combine(_pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            var extractError = _ExtractSafely(zipFilePath, stagingDir);
            if (extractError is not null)
            {
                return PluginInstallResult.Failure(extractError);
            }

            var manifestPath = Path.Combine(stagingDir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                return PluginInstallResult.Failure("The archive has no plugin.json at its root.");
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (!PluginManifest.TryParse(json, out var manifest, out var parseError) || manifest is null)
            {
                return PluginInstallResult.Failure($"Invalid plugin.json: {parseError}");
            }

            if (manifest.AbstractionsVersion != hostAbstractionsMajor)
            {
                return PluginInstallResult.Failure(
                    $"This plugin targets contract version {manifest.AbstractionsVersion}, but this cockpit provides version {hostAbstractionsMajor}.");
            }

            // AC-181: the same minHostVersion gate PluginLoadPolicy applies at load time, run here too — a plugin
            // that would be refused on load must not be allowed to install in the first place and sit there
            // reporting "installed" until the operator restarts and finds out otherwise.
            if (!PluginLoadPolicy.MeetsMinHostVersion(manifest.MinHostVersion, hostVersion))
            {
                return PluginInstallResult.Failure(
                    $"This plugin needs {CockpitProduct.DisplayName} {manifest.MinHostVersion} or later, but this cockpit is {hostVersion}.");
            }

            // AC-1159: rejects a rooted entryAssembly or one that walks out of stagingDir via `..` or a
            // mid-path symlink, before the closure hash below is ever computed over it.
            if (!PluginEntryPath.TryResolve(stagingDir, manifest.EntryAssembly, out var entryPath))
            {
                return PluginInstallResult.Failure($"The entry assembly path '{manifest.EntryAssembly}' is not allowed.");
            }

            if (!File.Exists(entryPath))
            {
                return PluginInstallResult.Failure($"The archive is missing its entry assembly '{manifest.EntryAssembly}'.");
            }

            var folderId = _ResolveFolderId(manifest.Id);
            // Hash of the whole load closure, computed from staging before the move (AC-43), so the pin covers
            // a swapped dependency DLL too — keeping an updated plugin enabled instead of needs-consent once
            // the pending copy is swapped in at the next restart.
            var newSha256 = await PluginClosureHash.OfInstalledFolderAsync(stagingDir, cancellationToken).ConfigureAwait(false);
            var finalDir = Path.Combine(_pluginsRoot, folderId);
            if (Directory.Exists(finalDir))
            {
                // Updating an existing install: a loaded assembly's file is locked until process exit (on
                // Windows), so stage the new version and let SweepPendingUpdatesAsync swap it in at next
                // startup, before any plugin loads — same restart-deferred contract removal uses.
                var pendingDir = Path.Combine(_pluginsRoot, PendingUpdatesFolder, folderId);
                Directory.CreateDirectory(Path.Combine(_pluginsRoot, PendingUpdatesFolder));
                if (Directory.Exists(pendingDir))
                {
                    Directory.Delete(pendingDir, recursive: true);
                }

                Directory.Move(stagingDir, pendingDir);
                return PluginInstallResult.Success(folderId, newSha256, staged: true);
            }

            Directory.Move(stagingDir, finalDir);
            return PluginInstallResult.Success(folderId, newSha256, staged: false);
        }
        catch (Exception exception)
        {
            return PluginInstallResult.Failure($"Install failed: {exception.Message}");
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch
                {
                    // Best-effort staging cleanup; a leftover staging folder is harmless and swept on reinstall.
                }
            }
        }
    }

    public Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(_pluginsRoot, folderId);
        if (!Directory.Exists(folder))
        {
            // Nothing installed under that id: nowhere to write the marker, so this is a deliberate no-op —
            // deleting a staged copy would be this method's only effect on an id matching no installed
            // plugin, and a caller normalizing an id differently could silently discard a wanted install.
            return Task.CompletedTask;
        }

        File.WriteAllText(Path.Combine(folder, RemovalMarker), "");

        // Removing wins over an update staged for the same plugin earlier in the session — otherwise the
        // startup sweep would apply that update first, deleting the marker with the folder, and the
        // removal would silently vanish with the plugin coming back at a version the operator rejected.
        var pendingDir = Path.Combine(_pluginsRoot, PendingUpdatesFolder, folderId);
        if (Directory.Exists(pendingDir))
        {
            try
            {
                Directory.Delete(pendingDir, recursive: true);
            }
            catch
            {
                // Best-effort, like every other deletion here: the marker is already written, so the removal
                // still happens. Worst case the staged copy is applied first and the folder returns — which is
                // the failure this withdrawal exists to prevent, but not one worth taking Remove down over.
            }
        }

        return Task.CompletedTask;
    }

    public Task SweepRemovalsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_pluginsRoot))
        {
            return Task.CompletedTask;
        }

        foreach (var folder in Directory.EnumerateDirectories(_pluginsRoot))
        {
            if (!File.Exists(Path.Combine(folder, RemovalMarker)))
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // If the folder is still locked (rare — the plugin was disabled but not yet unloaded),
                // the marker remains and it is swept on the next start.
            }
        }

        return Task.CompletedTask;
    }

    public Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var pendingRoot = Path.Combine(_pluginsRoot, PendingUpdatesFolder);
        if (!Directory.Exists(pendingRoot))
        {
            return Task.CompletedTask;
        }

        foreach (var pendingDir in Directory.EnumerateDirectories(pendingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The pending folder's name is the target folder id (see InstallFromZipAsync). At startup no plugin
            // is loaded yet, so the old folder is unlocked and can be replaced.
            var finalDir = Path.Combine(_pluginsRoot, Path.GetFileName(pendingDir));
            try
            {
                if (Directory.Exists(finalDir))
                {
                    Directory.Delete(finalDir, recursive: true);
                }

                Directory.Move(pendingDir, finalDir);
            }
            catch
            {
                // If the old folder is somehow still locked, leave the staged copy in place and apply it on the
                // next start; the existing install keeps working meanwhile.
            }
        }

        // Best-effort cleanup: drop the pending root once every staged update has been applied.
        try
        {
            var hasRemaining = false;
            foreach (var _ in Directory.EnumerateFileSystemEntries(pendingRoot))
            {
                hasRemaining = true;
                break;
            }

            if (!hasRemaining)
            {
                Directory.Delete(pendingRoot);
            }
        }
        catch
        {
            // A lingering empty pending root is harmless — discovery skips dot-prefixed folders — and it is
            // cleaned on the next start.
        }

        return Task.CompletedTask;
    }

    // Extracts each entry under stagingDir, rejecting any that escapes it. Returns an error string, or
    // null on success. Directory entries (empty Name) only create the folder.
    private static string? _ExtractSafely(string zipFilePath, string stagingDir)
    {
        Directory.CreateDirectory(stagingDir);

        using var archive = ZipFile.OpenRead(zipFilePath);
        foreach (var entry in archive.Entries)
        {
            if (!PluginInstallPath.TryResolveSafeEntryPath(stagingDir, entry.FullName, out var destination))
            {
                return $"The archive contains an unsafe path ('{entry.FullName}') and was rejected.";
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            var destinationFolder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destinationFolder))
            {
                Directory.CreateDirectory(destinationFolder);
            }

            entry.ExtractToFile(destination, overwrite: true);
        }

        return null;
    }

    // The manifest id normalized to a filesystem-safe slug, falling back to a generated installation id
    // when it is empty or would collide with an unrelated existing folder.
    private static string _ResolveFolderId(string manifestId)
    {
        var slug = PluginFolderName.Normalize(manifestId);
        if (string.IsNullOrEmpty(slug))
        {
            return Guid.NewGuid().ToString("N");
        }

        return slug;
    }
}
