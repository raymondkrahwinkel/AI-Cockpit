using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1435: composing a project is the project dialogs' own step and stays with the frontend; storing what it composes
// goes through IProjectEditor. Out of Core because its signature needs ISharedProjectSource.
/// <summary>
/// Composes a project the way the project dialogs would, for the assistant's tools, without showing a window.
/// </summary>
public interface IProjectComposer
{
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
    /// it, and a reason alongside when projects cannot be composed here at all.
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
}
