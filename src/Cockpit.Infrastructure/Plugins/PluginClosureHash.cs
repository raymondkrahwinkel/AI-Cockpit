using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Reads a plugin's files and folds them into one closure pin (AC-43), so a swapped dependency DLL — not just a
/// changed entry assembly — re-triggers consent. The <em>installed</em> and <em>source</em> variants exist so a
/// "did the source change vs what is installed" comparison hashes the same logical file set: a source build ships
/// files the installer never copies (the shared abstractions assembly, build sidecars in subfolders), so hashing
/// them raw would report a difference that copying would erase, and re-install forever.
/// </summary>
internal static class PluginClosureHash
{
    /// <summary>
    /// The closure of the files actually on disk under an installed plugin folder (what the loader will load):
    /// every file, recursively. This is what discovery verifies and what an install/consent pins.
    /// </summary>
    public static Task<string> OfInstalledFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        _ComputeAsync(folder, _EnumerateInstalledFiles(folder), cancellationToken);

    /// <summary>
    /// The closure of a source folder as it <em>would be</em> installed — mirroring
    /// <see cref="PluginSourceInstaller"/>'s copy selection (root files only, minus the shared abstractions
    /// assembly a plugin must not carry) so it can be compared against an installed folder's closure.
    /// </summary>
    public static Task<string> OfSourceFolderAsync(string folder, CancellationToken cancellationToken = default) =>
        _ComputeAsync(folder, _EnumerateSourceFiles(folder), cancellationToken);

    /// <summary>Whether a source file is copied into the install (and so counts towards the closure). Kept here so
    /// <see cref="PluginSourceInstaller"/>'s copy and this hash cannot drift apart.</summary>
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

    private static async Task<string> _ComputeAsync(string folder, IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var closure = new List<PluginClosureFile>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(folder, path);
            var sha = PluginHash.Compute(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            closure.Add(new PluginClosureFile(relative, sha));
        }

        return PluginHash.ComputeClosure(closure);
    }
}
