namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// Whether a directory is a git repository, as the host reports it to a plugin (AC-174). Deliberately three-valued
/// rather than a bool so the decision built on it is fail-closed: <see cref="Unknown"/> is the default a host that does
/// not implement the check returns, and a caller that isolates work in a worktree must treat it like
/// <see cref="Repository"/> (isolate / refuse rather than run free), never like <see cref="NotARepository"/>. Only a
/// host that positively answered <see cref="NotARepository"/> licenses running without isolation — so an older host,
/// or one that could not tell, never silently drops the isolation guard.
/// </summary>
public enum GitDirectoryStatus
{
    /// <summary>The host could not tell (it does not implement the check, or the probe failed) — treat as needing isolation.</summary>
    Unknown = 0,

    /// <summary>The directory is not a git repository — a caller may run there without worktree isolation.</summary>
    NotARepository,

    /// <summary>The directory is a git repository (or inside one) — work can be isolated in a worktree off it.</summary>
    Repository,
}
