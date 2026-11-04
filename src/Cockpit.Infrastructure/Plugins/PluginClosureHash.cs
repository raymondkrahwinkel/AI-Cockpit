using System.Collections.Concurrent;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// Reads a plugin's files into one closure pin (AC-43), so a swapped dependency DLL, not just the entry
// assembly, re-triggers consent. *Installed* and *source* variants hash the same logical file set — a source
// build ships files the installer never copies — so a raw comparison wouldn't false-positive on every install.
internal static class PluginClosureHash
{
    // The closure of the files actually on disk under an installed plugin folder (what the loader will load):
    // every file, recursively, every byte read. This is what startup discovery verifies and what an install pins.
    public static Task<string> OfInstalledFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        _ShaAsync(_ComputeAsync(folder, InstalledFiles(folder), reuseUnchanged: false, cancellationToken));

    // AC-2025: the same closure with the per-file hashes it was made of, so a loader can check each file it opens.
    public static Task<(string Sha, IReadOnlyList<PluginClosureFile> Files)> OfInstalledFolderWithFilesAsync(string folder, CancellationToken cancellationToken = default) =>
        _ComputeAsync(folder, InstalledFiles(folder), reuseUnchanged: false, cancellationToken);

    // AC-2018: the same closure for a rediscovery that loads and pins nothing (Options, the update check): a file whose
    // size and write time match the last full read keeps that hash. Never for a route that loads or compares a pin.
    public static Task<string> OfInstalledFolderForDisplayAsync(string folder, CancellationToken cancellationToken = default) =>
        _ShaAsync(_ComputeAsync(folder, InstalledFiles(folder), reuseUnchanged: true, cancellationToken));

    // The closure of a source folder as it *would be* installed — mirroring
    // `PluginSourceInstaller`'s copy selection (minus the root shared abstractions
    // assembly a plugin must not carry) so it can be compared against an installed folder's closure.
    public static Task<string> OfSourceFolderAsync(
        string folder,
        CancellationToken cancellationToken = default,
        SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
        _ShaAsync(_ComputeAsync(folder, SourceFiles(folder, searchOption), reuseUnchanged: false, cancellationToken));

    // Whether a source file is copied into the install (and so counts towards the closure). Kept here so
    // `PluginSourceInstaller`'s copy and this hash cannot drift apart.
    private static bool _IsCopiedSourceFile(string relativePath) =>
        !Path.GetFileName(relativePath).StartsWith('.')
        && (Path.GetDirectoryName(relativePath) is { Length: > 0 }
            || !relativePath.StartsWith("Cockpit.Plugins.Abstractions.", StringComparison.OrdinalIgnoreCase));

    // Every file under the folder, at any depth (dependency DLLs, native libs under runtimes/, the manifest),
    // except reserved dot-prefixed markers (.remove) — discovery skips dot-prefixed folders for the same reason,
    // and these never load as code.
    internal static IEnumerable<string> InstalledFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith('.'));

    // Child processes keep their own runtime closure, including any contract assemblies below the plugin root.
    public static IEnumerable<string> SourceFiles(string folder, SearchOption searchOption) =>
        Directory.EnumerateFiles(folder, "*", searchOption)
            .Where(path => _IsCopiedSourceFile(Path.GetRelativePath(folder, path)));

    // AC-2018: each rediscovery re-read ~260 MB of plugin bytes (0.4 s warm, 4–9 s once evicted from the file cache).
    // Filled by every full read, read only by `OfInstalledFolderForDisplayAsync`.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Sha)> _fileHashes =
        new(StringComparer.OrdinalIgnoreCase);

    private static async Task<string> _ShaAsync(Task<(string Sha, IReadOnlyList<PluginClosureFile> Files)> closure) => (await closure.ConfigureAwait(false)).Sha;

    private static async Task<(string Sha, IReadOnlyList<PluginClosureFile> Files)> _ComputeAsync(string folder, IEnumerable<string> files, bool reuseUnchanged, CancellationToken cancellationToken)
    {
        var closure = new List<PluginClosureFile>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, path);
            closure.Add(new PluginClosureFile(relative, await _FileShaAsync(path, reuseUnchanged, cancellationToken).ConfigureAwait(false)));
        }

        return (PluginHash.ComputeClosure(closure), closure);
    }

    private static async Task<string> _FileShaAsync(string path, bool reuseUnchanged, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (reuseUnchanged && _fileHashes.TryGetValue(path, out var known) && known.Length == info.Length && known.WrittenUtc == info.LastWriteTimeUtc)
        {
            return known.Sha;
        }

        var sha = PluginHash.Compute(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

        // Git's "racy clean" rule: a write time moves in clock ticks, so a file written moments ago could be rewritten
        // with the same size inside the same tick. Only a file that has been still for a while is remembered.
        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(2))
        {
            _fileHashes[path] = (info.Length, info.LastWriteTimeUtc, sha);
        }

        return sha;
    }
}
