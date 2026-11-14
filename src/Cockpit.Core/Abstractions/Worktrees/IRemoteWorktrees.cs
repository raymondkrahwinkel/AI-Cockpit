using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Abstractions.Worktrees;

/// <summary>
/// The bounded worktree operations offered by a connected server, without local filesystem paths.
/// </summary>
public interface IRemoteWorktrees
{
    /// <summary>
    /// The projects available for creating a worktree, from the latest refresh.
    /// </summary>
    IReadOnlyList<(string Id, string Name)> Projects { get; }

    /// <summary>
    /// Refreshes the connection's projects and reads their managed worktrees.
    /// </summary>
    Task<IReadOnlyList<RemoteManagedWorktree>> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a worktree in the named server project, using an optional label.
    /// </summary>
    Task CreateWorktreeAsync(string projectId, string? label, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a server worktree; the server enforces ownership and whether force is permitted.
    /// </summary>
    Task RemoveWorktreeAsync(RemoteManagedWorktree worktree, bool force, CancellationToken cancellationToken = default);
}
