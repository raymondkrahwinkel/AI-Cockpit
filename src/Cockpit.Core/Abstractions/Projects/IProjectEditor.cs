using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Projects;

// AC-1435: every write to the projects goes through here, from the Projects page and from a caller with no window
// alike, and IProjectCatalog.Changed tells the page about a write it did not make itself.
public interface IProjectEditor
{
    /// <summary>
    /// The stored project with <paramref name="projectId"/>, or null.
    /// </summary>
    Task<Project?> FindProjectAsync(string projectId);

    /// <summary>
    /// Stores a new project, with its logo copied into the cockpit's own logo folder, and returns it as stored.
    /// </summary>
    Task<Project> AddNewProjectAsync(Project project);

    /// <summary>
    /// Stores a project bound to a shared definition as <see cref="AddNewProjectAsync"/> does, then re-reads the
    /// shared projects so the definition it is bound to is no longer offered.
    /// </summary>
    Task<Project> AddBoundProjectAsync(Project project);

    /// <summary>
    /// Replaces the stored project with the same id, its logo copied as on add; null when there is none.
    /// </summary>
    Task<Project?> UpdateStoredProjectAsync(Project project);

    /// <summary>
    /// Records that a session just started on the project; false when it is no longer stored.
    /// </summary>
    Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt);

    /// <summary>
    /// Removes the project and its logo copy. Sessions already running under it are unaffected.
    /// </summary>
    Task RemoveProjectAsync(string projectId);
}
