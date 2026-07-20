namespace Cockpit.Core.Clones;

/// <summary>
/// A git repository the cockpit cloned from a URL into its own managed area (AC-90), so a session can be started on
/// a repository that is not yet on this machine. The registry of these — not the folders on disk — is the source of
/// truth for reuse and cleanup: a later start finds an already-cloned repository by its record rather than by
/// re-scanning the disk, and startup reconciliation forgets a record whose folder disappeared.
/// </summary>
/// <remarks>
/// A clone is a repository <em>root</em>, not a session's working tree: several sessions isolate off it with their
/// own worktrees (AC-85). So it is deliberately not owned by one session — it is shared, and outlives any single one.
/// </remarks>
public sealed record RepositoryClone(
    string Slug,
    string RemoteUrl,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);
