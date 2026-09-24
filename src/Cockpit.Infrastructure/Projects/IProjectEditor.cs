using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1375: kept out of Core for the same reason ISharedProjectSourceRegistry is — its signature needs
// ISharedProjectSource, a Plugins.Abstractions type Core carries no reference to.
/// <summary>
/// Adds and changes the cockpit's projects the way the project dialogs do, for callers with no window (AC-1375).
/// Stays behind the Projects page's own view model until that page refreshes on the store rather than its own mutations.
/// </summary>
public interface IProjectEditor
{
    /// <summary>
    /// The registered shared-project sources and the ids already bound or hidden on this machine, read as one instant.
    /// </summary>
    Task<(IReadOnlyList<ISharedProjectSource> Sources, IReadOnlySet<string> BoundIds, IReadOnlySet<string> HiddenIds)> ReadSharedProjectSourcesAsync();

    /// <summary>
    /// The project with <paramref name="projectId"/> as the Projects page holds it, or null.
    /// </summary>
    Task<Project?> FindProjectAsync(string projectId);

    /// <summary>
    /// Composes a local project bound to a shared definition, the "Add to my projects…" dialog's "Choose…" route
    /// with no window; null plus the reason when the definition cannot be read or the resource rows do not fit.
    /// </summary>
    Task<(Project? Project, string? Refusal)> ComposeSharedProjectAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken);

    /// <summary>
    /// Composes a new project the way the "New project" dialog would save it; null when the dialog could not save
    /// it, and a reason alongside when projects cannot be created here at all.
    /// </summary>
    Task<(Project? Project, string? Refusal)> ComposeNewProjectAsync(
        string name,
        string? description,
        string? sourceDirectory,
        string? behaviorPrompt,
        bool isolateInWorktreeByDefault,
        string? category,
        string? defaultProfileLabel,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a project bound to a shared definition and shows it on the Projects page.
    /// </summary>
    Task<Project> AddBoundProjectAsync(Project project);

    /// <summary>
    /// Stores a new project and shows it on the Projects page.
    /// </summary>
    Task<Project> AddNewProjectAsync(Project project);

    /// <summary>
    /// Replaces the stored project with the same id; null when there is none.
    /// </summary>
    Task<Project?> UpdateStoredProjectAsync(Project project);
}
