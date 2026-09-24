using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Services;

// AC-1375: `IProjectEditor` over the Projects page's own view model and the two project dialogs' composition. The
// page refreshes only on its own mutations, so a write straight to the store would never reach the screen.
internal sealed class ProjectEditorAdapter(
    CockpitViewModel cockpit,
    ISessionProfileStore profiles,
    ISharedProjectSourceRegistry? sharedProjectSources = null,
    IMcpServerCatalog? mcpServerCatalog = null) : IProjectEditor, ISingletonService
{
    // Registry and visibility filter read in one UI-thread hop: `SharedProjectSourceRegistry` is a plain dictionary
    // mutated on the UI thread by plugin settings screens, so reading it from a request thread would risk a torn read.
    public Task<(IReadOnlyList<ISharedProjectSource> Sources, IReadOnlySet<string> BoundIds, IReadOnlySet<string> HiddenIds)> ReadSharedProjectSourcesAsync() =>
        UiThreadCall.RunAsync(() =>
        {
            var (bound, hidden) = cockpit.Projects.SharedProjectVisibilityFilterIds();
            return ((IReadOnlyList<ISharedProjectSource>)(sharedProjectSources?.Sources ?? []), (IReadOnlySet<string>)bound, (IReadOnlySet<string>)hidden);
        });

    public Task<Project?> FindProjectAsync(string projectId) =>
        UiThreadCall.RunAsync(() => cockpit.Projects.Projects.FirstOrDefault(candidate => candidate.Id == projectId));

    // The "Choose…" route, not the "Clone…" one — so, exactly as `ApplyPickedDirectory` does for the operator's own
    // pick, the shared definition's `GitUrl` is dropped: the folder was pointed at rather than cloned from it.
    public async Task<(Project? Project, string? Refusal)> ComposeSharedProjectAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken)
    {
        var (viewModel, error) = await SharedProjectBindingDialogViewModel
            .CreateAsync(sharedProjectId, source.SourceName, source, profiles, cancellationToken).ConfigureAwait(false);
        if (viewModel is null)
        {
            // Definition read failed — unreachable, signed out, or the project gone since list_shared_projects —
            // passed on rather than summarised: "could not add it" tells the operator nothing they can act on.
            return (null, error);
        }

        viewModel.ApplyPickedDirectory(sourceDirectory);
        viewModel.SelectedProfileLabel = profileLabel;

        return _FillResourceRows(viewModel, resourceReferences) is { } rowRefusal
            ? (null, rowRefusal)
            : (viewModel.ToProject(), null);
    }

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

    public Task<Project> AddBoundProjectAsync(Project project) => UiThreadCall.RunAsync(() => cockpit.Projects.AddBoundProjectAsync(project));

    public Task<Project> AddNewProjectAsync(Project project) => UiThreadCall.RunAsync(() => cockpit.Projects.AddNewProjectAsync(project));

    public Task<Project?> UpdateStoredProjectAsync(Project project) =>
        UiThreadCall.RunAsync(() => cockpit.Projects.UpdateStoredProjectAsync(project));

    // AC-246: fills machine-specific resource rows the shared definition names but carries no reference for.
    // Positional, not keyed by label, since two rows can share a label. A blank row is silently dropped by the
    // dialog (fine when the operator sees the empty box); here nobody would, so it's refused with rows spelled out.
    private static string? _FillResourceRows(SharedProjectBindingDialogViewModel viewModel, IReadOnlyList<string>? references)
    {
        var rows = viewModel.ResourceRows;
        var given = references?.Where(reference => !string.IsNullOrWhiteSpace(reference)).ToList() ?? [];

        if (given.Count != rows.Count)
        {
            return rows.Count == 0
                ? "This project names no resources whose location is this machine's own, so there is nothing to pass in resources."
                : $"This project names {rows.Count} resource(s) whose location is this machine's own, and the shared definition does not carry them. "
                    + "Ask the operator for a local path for each, then call again with resources holding one entry per row, in this order: "
                    + string.Join("; ", rows.Select((row, index) => $"{index + 1}. {row.DisplayLabel}"))
                    + ".";
        }

        for (var index = 0; index < rows.Count; index++)
        {
            rows[index].Reference = given[index].Trim();
        }

        return null;
    }
}
