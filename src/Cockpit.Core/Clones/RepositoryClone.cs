namespace Cockpit.Core.Clones;

// A git repository the cockpit cloned from a URL into its own managed area (AC-90). The registry of
// these — not the folders on disk — is the source of truth for reuse and cleanup. A clone is a
// repository *root*, shared across sessions' own worktrees (AC-85), not owned by any single one.
public sealed record RepositoryClone(
    string Slug,
    string RemoteUrl,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);
