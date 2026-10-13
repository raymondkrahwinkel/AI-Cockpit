using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Services;

// AC-1435: what is left of AC-1375's editor adapter, the two project dialogs' composition without their windows.
// Storing what it composes goes through IProjectEditor, and the Projects page follows the catalog.
internal sealed class ProjectComposer(
    ISessionProfileStore profiles,
    IMcpServerCatalog? mcpServerCatalog = null) : ISingletonService
{
    // AC-1514: the composition itself lives in Infrastructure, where a server binds through it too.
    public Task<(Project? Project, string? Refusal)> ComposeSharedProjectAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken) =>
        SharedProjectComposition.ComposeAsync(sharedProjectId, source, sourceDirectory, profileLabel, resourceReferences, cancellationToken);

    // AC-799 review finding 8: production DI always registers a real `IMcpServerCatalog`, so the refusal is
    // unreachable there; kept rather than a no-op catalog that nothing past this line would ever actually read.
    public Task<(Project? Project, string? Refusal)> ComposeNewProjectAsync(
        string name,
        string? description,
        string? sourceDirectory,
        string? behaviorPrompt,
        bool isolateInWorktreeByDefault,
        string? category,
        string? defaultProfileLabel,
        CancellationToken cancellationToken)
    {
        if (mcpServerCatalog is null)
        {
            return Task.FromResult<(Project?, string?)>((null, "MCP servers are not available here, so a project cannot be created."));
        }

        return UiThreadCall.RunAsync(async () =>
        {
            var viewModel = await ProjectDialogViewModel.CreateAsync(
                null, profiles, mcpServerCatalog, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            viewModel.Name = name;
            viewModel.Description = description ?? string.Empty;
            viewModel.SourceDirectory = sourceDirectory ?? string.Empty;
            viewModel.BehaviorPrompt = behaviorPrompt ?? string.Empty;
            viewModel.IsolateInWorktreeByDefault = isolateInWorktreeByDefault;
            viewModel.Category = category ?? string.Empty;
            viewModel.SelectedProfileLabel = string.IsNullOrWhiteSpace(defaultProfileLabel) ? null : defaultProfileLabel.Trim();

            return ((Project?)(viewModel.CanSave ? viewModel.ToProject() : null), (string?)null);
        });
    }
}
