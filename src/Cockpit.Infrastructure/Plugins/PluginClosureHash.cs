using System.Collections.Concurrent;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

// Reads a plugin's files into one closure pin (AC-43), so a swapped dependency DLL, not just the entry
// assembly, re-triggers consent. *Installed* and *source* variants hash the same logical file set — a source
// build ships files the installer never copies — so a raw comparison wouldn't false-positive on every install.
internal static class PluginClosureHash
{
    // The closure of the files actually on disk under an installed plugin folder (what the loader will load):
    // every file, recursively. This is what discovery verifies and what an install/consent pins.
    public static Task<string> OfInstalledFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        _ComputeAsync(folder, _EnumerateInstalledFiles(folder), _installedFileHashes, cancellationToken);

    // The closure of a source folder as it *would be* installed — mirroring
    // `PluginSourceInstaller`'s copy selection (root files only, minus the shared abstractions
    // assembly a plugin must not carry) so it can be compared against an installed folder's closure.
    public static Task<string> OfSourceFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        _ComputeAsync(folder, _EnumerateSourceFiles(folder), cache: null, cancellationToken);

    // Whether a source file is copied into the install (and so counts towards the closure). Kept here so
    // `PluginSourceInstaller`'s copy and this hash cannot drift apart.
    public static bool IsCopiedSourceFile(string fileName) =>
        !fileName.StartsWith('.')
        && !fileName.StartsWith("Cockpit.Plugins.Abstractions.", StringComparison.OrdinalIgnoreCase);

    // Every file under the folder, at any depth (dependency DLLs, native libs under runtimes/, the manifest),
    // except reserved dot-prefixed markers (.remove) — discovery skips dot-prefixed folders for the same reason,
    // and these never load as code.
    private static IEnumerable<string> _EnumerateInstalledFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith('.'));

    private static IEnumerable<string> _EnumerateSourceFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(path => IsCopiedSourceFile(Path.GetFileName(path)));

    // AC-2018: each rediscovery re-read ~260 MB of plugin bytes (0.4 s warm, 4–9 s once evicted from the file cache).
    // Startup still hashes every byte, and plugins only load at startup, so what runs is always what was verified.
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Sha)> _installedFileHashes =
        new(StringComparer.OrdinalIgnoreCase);

    private static async Task<string> _ComputeAsync(
        string folder,
        IEnumerable<string> files,
        ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Sha)>? cache,
        CancellationToken cancellationToken)
    {
        var closure = new List<PluginClosureFile>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, path);
            closure.Add(new PluginClosureFile(relative, await _FileShaAsync(path, cache, cancellationToken).ConfigureAwait(false)));
        }

        return PluginHash.ComputeClosure(closure);
    }

    private static async Task<string> _FileShaAsync(
        string path,
        ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, string Sha)>? cache,
        CancellationToken cancellationToken)
    {
        if (cache is null)
        {
            return PluginHash.Compute(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }

        var info = new FileInfo(path);
        if (cache.TryGetValue(path, out var known) && known.Length == info.Length && known.WrittenUtc == info.LastWriteTimeUtc)
        {
            return known.Sha;
        }

        var sha = PluginHash.Compute(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));

        // Git's "racy clean" rule: a write time moves in clock ticks, so a file written moments ago could be rewritten
        // with the same size inside the same tick. Only a file that has been still for a while is remembered.
        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(2))
        {
            cache[path] = (info.Length, info.LastWriteTimeUtc, sha);
        }

        return sha;
    }
}
