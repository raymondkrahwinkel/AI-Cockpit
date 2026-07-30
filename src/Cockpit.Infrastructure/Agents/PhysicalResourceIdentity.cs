using System.Security;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// What "the same physical resource" means for AC-439's cross-desk collision monitor — the question the ticket
/// explicitly leaves open: a plain string comparison is not enough, because a worktree reached through two
/// different paths (a symlink, a relative vs. absolute spelling, a trailing separator) is one thing on disk claimed
/// under two spellings.
/// <para>
/// <strong>Scope: canonicalize path-shaped resources; leave everything else as written.</strong> A claim's
/// <c>resource</c> is free text an agent chose — a worktree path, a branch name, a file path — and the host never
/// interprets it (<see cref="Cockpit.Core.Abstractions.Agents.IAgentResourceClaims"/>'s own contract). This applies
/// filesystem canonicalization only to a resource that both looks like a rooted path and actually exists on disk at
/// that path; a branch name or any other free text is compared exactly, unchanged from AC-393's own rule, because
/// there is nothing to resolve it against.
/// </para>
/// <para>
/// <strong>Partial, not a full <c>realpath</c> — and that is the accepted phase-1 gap.</strong>
/// <see cref="File.ResolveLinkTarget"/> collapses a symlink at the leaf of the path (the case the ticket names: a
/// worktree reached through a symlinked directory), but .NET has no cross-platform primitive that resolves a
/// symlink sitting in the <em>middle</em> of a path's segments, and reimplementing one segment-by-segment is more
/// surface than this phase needs. Two spellings that differ only by an intermediate symlink will not collapse to one
/// signal; two spellings of the same leaf path — the shape a worktree path or a cloned checkout actually takes —
/// will.
/// </para>
/// </summary>
internal static class PhysicalResourceIdentity
{
    /// <summary>
    /// The identity a resource collides under. Path-shaped and present on disk: the full, resolved path (case
    /// folded on Windows, where two spellings of one path are routinely different case). Anything else, including a
    /// path-shaped resource that does not currently exist (a worktree already removed, say): the string exactly as
    /// claimed, so a resource this cannot canonicalize still participates in collision detection under its own
    /// spelling rather than being silently excluded.
    /// </summary>
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
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                fullPath = File.ResolveLinkTarget(fullPath, returnFinalTarget: true)?.FullName ?? fullPath;
            }

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
