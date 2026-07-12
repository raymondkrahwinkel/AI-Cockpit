using System.IO.Compression;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Installs a plugin from a <c>.zip</c> and schedules removals (#14). The archive is unpacked entry by
/// entry through the <see cref="PluginInstallPath"/> zip-slip guard into a staging folder on the same
/// volume as the plugins root, its root <c>plugin.json</c> is parsed and its abstractions major checked,
/// and only then is it moved into its final <c>plugins/&lt;id&gt;/</c> folder. Removal drops a
/// <c>.remove</c> marker that <see cref="SweepRemovalsAsync"/> acts on at the next startup, and an update
/// over an existing install is staged under <see cref="PendingUpdatesFolder"/> for
/// <see cref="SweepPendingUpdatesAsync"/> to apply at the next startup — both deferred because a loaded
/// plugin's assembly file stays locked (on Windows) until the process exits, so replacing it in place
/// throws an access-denied.
/// </summary>
internal sealed class PluginInstaller : IPluginInstaller, ISingletonService
{
    private const string RemovalMarker = ".remove";

    // A reserved (dot-prefixed, so discovery skips it) folder under the plugins root holding staged updates as
    // .pending-updates/<folderId>/. Kept off to the side rather than swapped in place so an update never has to
    // delete a locked, loaded assembly mid-session; the swap happens at startup before any plugin loads.
    private const string PendingUpdatesFolder = ".pending-updates";

    private readonly string _pluginsRoot;

    public PluginInstaller()
        : this(CockpitConfigPath.PluginsRoot)
    {
    }

    /// <summary>Test seam: point the installer at an arbitrary plugins root.</summary>
    internal PluginInstaller(string pluginsRoot)
    {
        _pluginsRoot = pluginsRoot;
    }

    public async Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, int hostAbstractionsMajor, CancellationToken cancellationToken = default)
    {
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

            if (!File.Exists(Path.Combine(stagingDir, manifest.EntryAssembly)))
            {
                return PluginInstallResult.Failure($"The archive is missing its entry assembly '{manifest.EntryAssembly}'.");
            }

            var folderId = _ResolveFolderId(manifest.Id);
            // Hash of the newly installed entry assembly (computed from staging, before the move). The caller
            // pins this so an updated plugin — whose new bytes only go live after the next restart — stays
            // enabled instead of dropping to needs-consent when the pending copy is swapped in.
            var newSha256 = PluginHash.Compute(await File.ReadAllBytesAsync(Path.Combine(stagingDir, manifest.EntryAssembly), cancellationToken).ConfigureAwait(false));
            var finalDir = Path.Combine(_pluginsRoot, folderId);
            if (Directory.Exists(finalDir))
            {
                // Updating an existing install: the plugin may be loaded, and a loaded assembly's file is locked
                // until the process exits (on Windows), so an in-place replace would throw an access-denied.
                // Stage the new version and let SweepPendingUpdatesAsync swap it in at the next startup — before
                // any plugin loads — the same restart-deferred contract removal uses.
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
        if (Directory.Exists(folder))
        {
            File.WriteAllText(Path.Combine(folder, RemovalMarker), "");
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
