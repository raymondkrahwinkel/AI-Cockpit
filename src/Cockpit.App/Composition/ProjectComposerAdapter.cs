using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Composition;

// AC-1441: the backend's project composer is the desktop's dialogs; IProjectComposer carries a plugin SDK type.
internal sealed class ProjectComposerAdapter(ProjectComposer composer) : IProjectComposer, ISingletonService
{
    public Task<(Project? Project, string? Refusal)> ComposeSharedProjectAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken) =>
        composer.ComposeSharedProjectAsync(sharedProjectId, source, sourceDirectory, profileLabel, resourceReferences, cancellationToken);

    public Task<(Project? Project, string? Refusal)> ComposeNewProjectAsync(
        string name,
        string? description,
        string? sourceDirectory,
        string? behaviorPrompt,
        bool isolateInWorktreeByDefault,
        string? category,
        string? defaultProfileLabel,
        CancellationToken cancellationToken) =>
        composer.ComposeNewProjectAsync(
            name, description, sourceDirectory, behaviorPrompt, isolateInWorktreeByDefault, category, defaultProfileLabel, cancellationToken);
}
