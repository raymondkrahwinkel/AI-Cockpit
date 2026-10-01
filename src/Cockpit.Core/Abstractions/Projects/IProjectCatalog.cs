using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Projects;

// AC-1435: what the Projects page shows, read as one snapshot. Every write through IProjectEditor, a shared-project
// read and a Depot sync check replace it and raise Changed, on whichever thread made the change.
public interface IProjectCatalog
{
    /// <summary>
    /// The projects, the shared-project sources and what they offer, the ownership claims and the Depot sync state, as last read.
    /// </summary>
    ProjectCatalogSnapshot Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> was replaced.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Re-reads the stored projects, so an edit made outside the cockpit is reflected rather than overwritten.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks every shared-project source what it offers and claims the local projects bound to it. A newer call
    /// supersedes one still running; a source that fails shows its error in its own group.
    /// </summary>
    Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks one Depot-bound project for a change made elsewhere now, outside the watcher's own interval.
    /// </summary>
    Task SyncNowAsync(string projectId);
}
