using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// AC-2025: the copy of a plugin folder that is hashed and loaded, `<state>/plugin-shadow/<id>/<closure-sha>/`. A loaded
// DLL is locked on Windows, so loading from `plugins/<id>` would pin the installed folder; loading from the copy leaves
// it free to be overwritten, and the bytes that are hashed are the bytes that are loaded (no gap for a swap in between).
// The pin is checked against the copy, never the original. The copy is not trusted by its name either: a folder that
// is already there is hashed again, and the load context re-checks every file it opens against `Files`.
public sealed class PluginShadow
{
    private const string ShadowFolder = "plugin-shadow";

    private static readonly ConcurrentDictionary<(string FolderId, string Sha256), Lazy<PluginShadow>> _prepared = new();

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

    // The copy of an approved plugin, made and verified once per (folder id, closure hash). Throws
    // `PluginIntegrityException` when the bytes copied are not the bytes the pin approved.
    public static PluginShadow GetOrPrepare(DiscoveredPlugin discovered)
    {
        var key = (discovered.FolderId, discovered.Sha256);
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

    // Removes every copy that is not one of `keep`, and the folders of plugins that are gone. Best effort: a copy that
    // is still locked stays for the next start.
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
            foreach (var copy in Directory.EnumerateDirectories(idFolder).Where(copy => !kept.Contains(Path.GetFileName(copy).ToLowerInvariant())))
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
        var copy = Path.Combine(RootFor(Path.GetDirectoryName(Path.GetFullPath(discovered.FolderPath))!), discovered.FolderId, discovered.Sha256.ToLowerInvariant());

        // A copy left by an earlier start is used when it still hashes to the pin; anything else is thrown away.
        if (Directory.Exists(copy))
        {
            if (_VerifiedAsync(copy, discovered.Sha256).GetAwaiter().GetResult() is { } reused)
            {
                return reused;
            }

            Directory.Delete(copy, recursive: true);
        }

        _EnsureRoom(discovered.FolderPath, copy);
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var path in PluginClosureHash.InstalledFiles(discovered.FolderPath))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(discovered.FolderPath, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }

            return _VerifiedAsync(copy, discovered.Sha256).GetAwaiter().GetResult()
                ?? throw new PluginIntegrityException("The plugin files changed; discover and approve them again before loading.");
        }
        catch
        {
            _TryDelete(copy);
            throw;
        }
    }

    // The hash is taken from the copy itself, which is the only thing that is loaded afterwards.
    private static async Task<PluginShadow?> _VerifiedAsync(string copy, string pinnedSha256)
    {
        var (sha, files) = await PluginClosureHash.OfInstalledFolderWithFilesAsync(copy).ConfigureAwait(false);
        if (!string.Equals(sha, pinnedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return new PluginShadow(copy, files.ToDictionary(file => Path.GetFullPath(Path.Combine(copy, file.RelativePath)), file => file.Sha256, comparer));
    }

    // The Browser plugin is hundreds of MB: say so up front instead of failing halfway through the copy.
    private static void _EnsureRoom(string source, string copy)
    {
        var needed = PluginClosureHash.InstalledFiles(source).Sum(path => new FileInfo(path).Length);
        long free;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(copy))!).AvailableFreeSpace;
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
