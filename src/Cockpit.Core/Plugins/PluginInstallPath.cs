namespace Cockpit.Core.Plugins;

/// <summary>
/// Zip-slip guard for plugin installation (#14): resolves a zip entry to an absolute path under the
/// destination root and rejects anything that escapes it (a <c>../</c> traversal). Pure, so the guard
/// is unit-tested without touching disk. The caller extracts only entries this accepts.
/// </summary>
public static class PluginInstallPath
{
    public static bool TryResolveSafeEntryPath(string destinationRoot, string entryFullName, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(entryFullName))
        {
            return false;
        }

        var rootFull = Path.GetFullPath(destinationRoot);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, entryFullName));
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        resolvedPath = candidate;
        return true;
    }
}
