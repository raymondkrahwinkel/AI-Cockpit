using Cockpit.Infrastructure.Delegation;

namespace Cockpit.Infrastructure.Plugins;

// AC-1159: `Path.Combine` drops its first argument for a rooted entryAssembly and walks out of it for a `..`,
// so all three consumers resolve the combined path through here before hashing or loading it. Reuses
// FilesystemPath (AC-1160), not a second lexical canonicalisation, so a mid-path symlink is caught too.

// Public rather than internal: PluginActivator's assembly (Cockpit.App) gets a production reference to
// this assembly but no InternalsVisibleTo grant, unlike the test assemblies FilesystemPath allows.
public static class PluginEntryPath
{
    public static bool TryResolve(string folder, string entryAssembly, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        string candidate;
        try
        {
            candidate = Path.Combine(folder, entryAssembly);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (FilesystemPath.Canonicalize(folder) is not { } rootFull ||
            FilesystemPath.Canonicalize(candidate) is not { } candidateFull)
        {
            return false;
        }

        // Ordinal, with the separator: a lexical prefix match would let ".../plugins/foo-evil" through
        // against root ".../plugins/foo".
        if (!candidateFull.Equals(rootFull, StringComparison.Ordinal) &&
            !candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        resolvedPath = candidateFull;
        return true;
    }
}
