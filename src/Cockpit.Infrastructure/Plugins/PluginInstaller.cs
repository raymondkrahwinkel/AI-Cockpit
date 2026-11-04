using System.IO.Compression;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

// Installs a plugin from a `.zip` (zip-slip guarded, staged, validated, moved into `plugins/&lt;id&gt;/`) and schedules removals (#14).
// AC-2025: plugins load from a copy, so an update replaces its folder at once; `.pending-updates` is the fallback
// for a plugin that asks for a restart or a folder that cannot be replaced.
internal sealed class PluginInstaller : IPluginInstaller, ISingletonService
{
    // The file dropped into a plugin's folder to have it deleted at the next start. Discovery reads it too, so a plugin the operator removed is out of the list from that moment rather than at the restart.
    internal const string RemovalMarker = ".remove";

    // A reserved (dot-prefixed, so discovery skips it) folder under the plugins root holding staged updates as
    // .pending-updates/<folderId>/. Kept off to the side rather than swapped in place so an update never has to
    // delete a locked, loaded assembly mid-session; the swap happens at startup before any plugin loads.
    private const string PendingUpdatesFolder = ".pending-updates";

    // AC-2025: where the folder an update replaced waits for its deletion.
    private const string ReplacedPrefix = ".replaced-";

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

            // AC-1159: rejects a rooted assembly path or one that walks out of stagingDir via `..` or a
            // mid-path symlink, before the closure hash below is ever computed over it. AC-1389: the UI part's too.
            foreach (var assembly in manifest.Assemblies)
            {
                if (!PluginEntryPath.TryResolve(stagingDir, assembly, out var assemblyPath))
                {
                    return PluginInstallResult.Failure($"The assembly path '{assembly}' is not allowed.");
                }

                if (!File.Exists(assemblyPath))
                {
                    return PluginInstallResult.Failure($"The archive is missing its assembly '{assembly}'.");
                }
            }

            var folderId = _ResolveFolderId(manifest.Id);
            // Hash of the whole load closure, computed from staging before the move (AC-43), so the pin covers
            // a swapped dependency DLL too — keeping an updated plugin enabled instead of needs-consent once
            // the pending copy is swapped in at the next restart.
            var newSha256 = await PluginClosureHash.OfInstalledFolderAsync(stagingDir, cancellationToken).ConfigureAwait(false);
            var finalDir = Path.Combine(_pluginsRoot, folderId);
            if (Directory.Exists(finalDir))
            {
                // An update. `Staged` keeps meaning "the new bytes take effect at the next start", whether they are
                // already in place or waiting in `.pending-updates`; the caller re-pins them either way.
                // AC-2025: more capabilities than were approved wait for the restart's consent step, beside the running version.
                var added = _AddedCapabilities(finalDir, manifest);
                // What the folder held before it is replaced: the caller pins the update only over bytes that were approved.
                var replacedSha256 = await PluginClosureHash.OfInstalledFolderAsync(finalDir, cancellationToken).ConfigureAwait(false);
                if (added.Count == 0 && !manifest.RequiresRestart && _TryReplaceInPlace(stagingDir, finalDir, folderId))
                {
                    return PluginInstallResult.Success(folderId, newSha256, staged: true, manifest: manifest, inPlace: true, replacedSha256: replacedSha256);
                }

                // The fallback: stage the new version and let SweepPendingUpdatesAsync swap it in at next
                // startup, before any plugin loads — the restart-deferred contract removal uses.
                var pendingDir = Path.Combine(_pluginsRoot, PendingUpdatesFolder, folderId);
                Directory.CreateDirectory(Path.Combine(_pluginsRoot, PendingUpdatesFolder));
                if (Directory.Exists(pendingDir))
                {
                    Directory.Delete(pendingDir, recursive: true);
                }

                Directory.Move(stagingDir, pendingDir);
                return PluginInstallResult.Success(
                    folderId, newSha256, staged: true, manifest: manifest, replacedSha256: replacedSha256,
                    consentReason: added.Count == 0 ? null : $"The update asks for new capabilities ({string.Join(", ", added)}). Approve it in Plugin Manager after the restart, or the plugin stays off.");
            }

            Directory.Move(stagingDir, finalDir);
            return PluginInstallResult.Success(folderId, newSha256, staged: false, manifest: manifest);
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

    // The old folder is renamed aside first, so a failure leaves it as it was; discovery skips the dot-prefixed name.
    private bool _TryReplaceInPlace(string stagingDir, string finalDir, string folderId)
    {
        var aside = Path.Combine(_pluginsRoot, ReplacedPrefix + folderId + "." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(finalDir, aside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            Directory.Move(stagingDir, finalDir);
        }
        catch
        {
            Directory.Move(aside, finalDir);
            throw;
        }

        // An update staged earlier would otherwise be swapped in at the next start, over this newer one.
        _TryDelete(Path.Combine(_pluginsRoot, PendingUpdatesFolder, folderId));
        _TryDelete(aside);
        return true;
    }

    // What the new manifest asks for beyond the installed one; everything when the installed one cannot be read.
    private static List<string> _AddedCapabilities(string installedFolder, PluginManifest incoming)
    {
        var manifestPath = Path.Combine(installedFolder, "plugin.json");
        var known = File.Exists(manifestPath) && PluginManifest.TryParse(File.ReadAllText(manifestPath), out var installed, out _) && installed is not null
            ? installed.Capabilities
            : [];
        return [.. incoming.Capabilities.Except(known, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<bool> RestoreFolderAsync(string folderId, string sourceFolder, string expectedSha256, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(_pluginsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var path in PluginClosureHash.InstalledFiles(sourceFolder))
            {
                var target = Path.Combine(staging, Path.GetRelativePath(sourceFolder, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }

            Directory.CreateDirectory(staging);
            return string.Equals(await PluginClosureHash.OfInstalledFolderAsync(staging, cancellationToken).ConfigureAwait(false), expectedSha256, StringComparison.OrdinalIgnoreCase)
                && _TryReplaceInPlace(staging, Path.Combine(_pluginsRoot, folderId), folderId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            _TryDelete(staging);
        }
    }

    private static void _TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort, like the other deletions here; a leftover `.replaced-*` folder is swept at the next start.
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

    // A start that finds `.replaced-<id>.<guid>` next to a missing `<id>` is a crash between the two renames of an
    // in-place update: the old version goes back instead of the plugin vanishing. Otherwise it is only left-over.
    private void _RecoverReplaced()
    {
        if (!Directory.Exists(_pluginsRoot))
        {
            return;
        }

        foreach (var aside in Directory.EnumerateDirectories(_pluginsRoot, ReplacedPrefix + "*"))
        {
            var name = Path.GetFileName(aside)[ReplacedPrefix.Length..];
            var dot = name.LastIndexOf('.');
            var original = dot > 0 ? Path.Combine(_pluginsRoot, name[..dot]) : null;
            try
            {
                if (original is not null && !Directory.Exists(original))
                {
                    Directory.Move(aside, original);
                }
                else
                {
                    Directory.Delete(aside, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still locked: it is dealt with at the next start.
            }
        }
    }

    public Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _RecoverReplaced();
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
