using System.Security;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: AC-439 needs "same physical resource" identity for path-shaped claims only (other free text
// compares exactly, per AC-393); this is a partial, leaf-symlink-only canonicalization, an accepted phase-1 gap
// vs. a full realpath (.NET has no cross-platform mid-path symlink resolver).
internal static class PhysicalResourceIdentity
{
    // AC-1013: collision identity. Path-shaped + on disk: resolved path, case-folded on Windows. Otherwise
    // (including a path that no longer exists): the claimed string as-is, so it still collides under its spelling.
    internal static string Canonicalize(string resource)
    {
        if (!Path.IsPathRooted(resource))
        {
            return resource;
        }

        try
        {
            // GetFullPath resolves ".." and "." but, on Linux/macOS, leaves a trailing separator exactly as given —
            // "/repo/worktree-a" and "/repo/worktree-a/" name one directory and must canonicalize to one string.
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resource));
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                // AC-1013: nothing on disk to resolve, so return as-claimed. Case-folding here anyway made
                // Windows and Linux disagree on the identity of the same claimed string.
                return resource;
            }

            fullPath = File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;

            // Case folding belongs to a path that was actually resolved: it is a statement about the filesystem
            // this path was just found on, where two spellings differing only in case are one entry.
            return OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        {
            // A path that cannot be resolved (permissions, a device path, a race with something deleting it) is not
            // grounds to drop the resource out of collision detection — it is compared under the raw string
            // instead, same as a resource this function never attempts to touch.
            return resource;
        }
    }
}
