namespace Cockpit.Core.Depot;

// A local mirror of a Depot project's folder (AC-278). The registry of these — not the folders on disk — is
// the source of truth for cleanup, the same convention `RepositoryClone`/`WorktreeRecord` use: a crash can
// leave a mirror behind without ever running the teardown that would have removed it.
public sealed record DepotMirror(
    string InstanceHost,
    string Slug,
    string Path,
    DateTimeOffset CreatedAt)
{
    // Set when disabling or removing the mirror found local content it could not prove was already synced
    // elsewhere: shown for review, never auto-removed and never deleted from disk — the worktree's
    // `IsRetained` under a different name, same cleanup-policy-A discipline.
    public bool IsRetained { get; init; }
}
