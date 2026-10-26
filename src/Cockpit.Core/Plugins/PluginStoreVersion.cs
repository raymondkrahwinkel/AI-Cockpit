using System.Runtime.InteropServices;

namespace Cockpit.Core.Plugins;

// One published version of a store plugin: the version string, the repo-relative `Path` to its
// zip, the contract/host versions it targets, an optional `Sha256` of the zip for integrity
// verification on download, optional release notes, and optional per-RID `Packages` (AC-1740).
public sealed record PluginStoreVersion(
    string Version,
    string Path,
    int? AbstractionsVersion,
    string? MinHostVersion,
    string? Sha256,
    string? Notes,
    IReadOnlyDictionary<string, PluginStorePackage>? Packages = null)
{
    // The zip this host downloads: the package for its RID, `Path` when the version has no packages,
    // and null when it has packages but none for this RID.
    public PluginStorePackage? PackageFor(string? runtimeIdentifier = null)
    {
        if (Packages is null)
        {
            return new PluginStorePackage(Path, Sha256);
        }

        return Packages.GetValueOrDefault(runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier);
    }
}

// One platform's zip of a version (AC-1740): its repo-relative path and the sha256 to verify it against.
public sealed record PluginStorePackage(string Path, string? Sha256);
