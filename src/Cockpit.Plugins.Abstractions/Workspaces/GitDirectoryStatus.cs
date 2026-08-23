namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// Whether a directory is a git repository, as the host reports it to a plugin (AC-174). Deliberately
/// three-valued rather than a bool so the decision built on it is fail-closed.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the default for a host that does not implement the check; a caller must treat it
/// like <see cref="Repository"/> (isolate) rather than <see cref="NotARepository"/>.
/// </remarks>
public enum GitDirectoryStatus
{
    /// <summary>
    /// The host could not tell (it does not implement the check, or the probe failed) — treat as needing isolation.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The directory is not a git repository — a caller may run there without worktree isolation.
    /// </summary>
    NotARepository,

    /// <summary>
    /// The directory is a git repository (or inside one) — work can be isolated in a worktree off it.
    /// </summary>
    Repository,
}
