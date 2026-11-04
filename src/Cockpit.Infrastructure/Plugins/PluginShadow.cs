using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Delegation;

namespace Cockpit.Infrastructure.Plugins;

// AC-2025: the copy of a plugin folder that is hashed and loaded, `<state>/plugin-shadow/<id>/<closure-sha>/`. A loaded
// DLL is locked on Windows, so the copy keeps `plugins/<id>` free, and the pin is checked against the bytes that load.
// A copy is never trusted by its name (an existing one is hashed again) and the load context re-checks every file.
public sealed class PluginShadow
{
    private const string ShadowFolder = "plugin-shadow";

    // Held open for the life of the process in every copy it uses: the sweep of another cockpit (a restart overlaps the
    // old process, a server may share the root) finds it locked and leaves the copy alone. Dot-prefixed, so not hashed.
    private const string InUseMarker = ".in-use";

    // A copy or staging folder touched this recently may belong to a process that has not marked it yet.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<(string FolderId, string Sha256), Lazy<PluginShadow>> _prepared = new();

    private FileStream? _inUse;

    private PluginShadow(string folder, IReadOnlyDictionary<string, string> files)
    {
        Folder = folder;
        Files = files;
    }

    // The verified copy's folder.
    public string Folder { get; }

    // Full path of every file in the copy to the SHA-256 it had when the closure was verified.
    public IReadOnlyDictionary<string, string> Files { get; }

    // Throws unless `path` is a file of the copy that still hashes to what was approved. Called right before a file is loaded.
    public void Verify(string path)
    {
        if (!Files.TryGetValue(Path.GetFullPath(path), out var expected)
            || !string.Equals(_FileSha(path), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new PluginIntegrityException($"'{Path.GetFileName(path)}' is not the file that was approved for this plugin; it was not loaded.");
        }
    }

    // The copy of an approved plugin, made and verified once per (folder id, closure hash). `fresh` hashes it again
    // instead of trusting the one made earlier in this process. Throws `PluginIntegrityException` when the bytes
    // copied are not the bytes the pin approved.
    public static PluginShadow GetOrPrepare(DiscoveredPlugin discovered, bool fresh = false)
    {
        var key = (discovered.FolderId, discovered.Sha256);
        if (fresh)
        {
            _prepared.TryRemove(key, out _);
        }

        try
        {
            return _prepared.GetOrAdd(key, _ => new Lazy<PluginShadow>(() => _Prepare(discovered))).Value;
        }
        catch
        {
            // A failure (changed files, a full disk) is not remembered: the next attempt prepares again.
            _prepared.TryRemove(key, out _);
            throw;
        }
    }

    // Removes the copies that are not one of `keep` and not in use by a live process, and the folders of plugins that
    // are gone. The newest other copy of a kept plugin stays: it is what a failed update rolls back to. Best effort.
    public static void Sweep(string pluginsRoot, IEnumerable<DiscoveredPlugin> keep)
    {
        var root = RootFor(pluginsRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        var keepers = keep.ToLookup(plugin => plugin.FolderId, plugin => plugin.Sha256.ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
        foreach (var idFolder in Directory.EnumerateDirectories(root))
        {
            var kept = keepers[Path.GetFileName(idFolder)].ToHashSet();
            var others = Directory.EnumerateDirectories(idFolder).Where(copy => !kept.Contains(Path.GetFileName(copy).ToLowerInvariant())).ToList();
            var rollback = kept.Count == 0
                ? null
                : others.Where(copy => !Path.GetFileName(copy).StartsWith('.')).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
            foreach (var copy in others.Where(copy => copy != rollback && !_IsInUse(copy)))
            {
                _TryDelete(copy);
            }

            if (!Directory.EnumerateFileSystemEntries(idFolder).Any())
            {
                _TryDelete(idFolder);
            }
        }
    }

    // Next to the plugins root, so a state root of its own (a dev build, a test) has its own copies.
    internal static string RootFor(string pluginsRoot) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginsRoot)))!, ShadowFolder);

    private static PluginShadow _Prepare(DiscoveredPlugin discovered)
    {
        var idFolder = Path.Combine(RootFor(Path.GetDirectoryName(Path.GetFullPath(discovered.FolderPath))!), discovered.FolderId);
        Directory.CreateDirectory(idFolder);

        // Canonical, as the paths the entry assemblies are resolved to are: a link in the state root must not refuse every plugin.
        idFolder = FilesystemPath.Canonicalize(idFolder) ?? throw new IOException($"The plugin copy folder '{idFolder}' could not be resolved.");
        var copy = Path.Combine(idFolder, discovered.Sha256.ToLowerInvariant());

        // A copy left by an earlier start (or another process) is used when it still hashes to the pin; anything else is thrown away.
        if (Directory.Exists(copy))
        {
            if (_Verified(copy, copy, discovered.Sha256) is { } reused)
            {
                return reused._Hold();
            }

            Directory.Delete(copy, recursive: true);
        }

        _EnsureRoom(discovered.FolderPath, idFolder);

        // Built beside the final folder and renamed into place, so another process never meets a half-made copy.
        var staging = Path.Combine(idFolder, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var path in PluginClosureHash.InstalledFiles(discovered.FolderPath))
            {
                var target = Path.Combine(staging, Path.GetRelativePath(discovered.FolderPath, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }

            Directory.CreateDirectory(staging);
            var verified = _Verified(staging, copy, discovered.Sha256)
                ?? throw new PluginIntegrityException("The plugin files changed; discover and approve them again before loading.");
            try
            {
                Directory.Move(staging, copy);
            }
            catch (IOException) when (Directory.Exists(copy))
            {
                // Another process published the same copy first: use it only if it is the approved one.
                verified = _Verified(copy, copy, discovered.Sha256)
                    ?? throw new PluginIntegrityException("The plugin files changed; discover and approve them again before loading.");
            }

            return verified._Hold();
        }
        finally
        {
            _TryDelete(staging);
        }
    }

    // The hash is taken from the copy itself, which is the only thing that is loaded afterwards. `hashed` and
    // `located` differ only while the copy is still in its staging folder.
    private static PluginShadow? _Verified(string hashed, string located, string pinnedSha256)
    {
        var (sha, files) = PluginClosureHash.OfInstalledFolderWithFilesAsync(hashed).GetAwaiter().GetResult();
        if (!string.Equals(sha, pinnedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new PluginShadow(located, files.ToDictionary(file => Path.GetFullPath(Path.Combine(located, file.RelativePath)), file => file.Sha256, comparer));
    }

    private PluginShadow _Hold()
    {
        _inUse = new FileStream(Path.Combine(Folder, InUseMarker), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        return this;
    }

    private static bool _IsInUse(string copy)
    {
        if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(copy) < Grace)
        {
            return true;
        }

        try
        {
            using var exclusive = new FileStream(Path.Combine(copy, InUseMarker), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The Browser plugin is hundreds of MB: say so up front instead of failing halfway through the copy.
    private static void _EnsureRoom(string source, string copyFolder)
    {
        var needed = PluginClosureHash.InstalledFiles(source).Sum(path => new FileInfo(path).Length);
        long free;
        try
        {
            free = _FreeSpace(copyFolder);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (free < needed)
        {
            throw new IOException($"Not enough disk space to load the plugin: it needs {needed / 1_048_576} MB and {free / 1_048_576} MB is free.");
        }
    }

    // The volume holding `folder`: the longest mount point that prefixes it (`GetPathRoot` is only `/` on Linux).
    private static long _FreeSpace(string folder) =>
        DriveInfo.GetDrives()
            .Where(drive => folder.StartsWith(drive.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .Select(drive => drive.AvailableFreeSpace)
            .DefaultIfEmpty(long.MaxValue)
            .First();

    private static void _TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // SHA-256 of one file, streamed (a native library can be large), in the form the closure hash uses.
    private static string _FileSha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

// The bytes about to be loaded are not the bytes that were approved.
public sealed class PluginIntegrityException(string message) : InvalidOperationException(message);
