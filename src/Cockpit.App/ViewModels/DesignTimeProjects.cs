using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// Projects that live only in this object, for the Avalonia previewer's parameterless `ProjectsViewModel`. The
// previewer has no DI container and must never touch the operator's real `cockpit.json` — rendering a design-time
// surface is not a reason to read or write their config. AC-1435: was a project store, now the catalog the page reads.
internal sealed class DesignTimeProjects : IProjectCatalog, IProjectEditor
{
    public ProjectCatalogSnapshot Current { get; private set; } = ProjectCatalogSnapshot.Empty;

    public event Action? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SyncNowAsync(string projectId) => Task.CompletedTask;

    public Task<Project?> FindProjectAsync(string projectId) =>
        Task.FromResult(Current.Settings.Projects.FirstOrDefault(project => project.Id == projectId));

    public Task<Project> AddNewProjectAsync(Project project)
    {
        _Store(Current.Settings.WithProject(project));
        return Task.FromResult(project);
    }

    public Task<Project> AddBoundProjectAsync(Project project) => AddNewProjectAsync(project);

    public Task<Project?> UpdateStoredProjectAsync(Project project)
    {
        if (Current.Settings.Projects.All(candidate => candidate.Id != project.Id))
        {
            return Task.FromResult<Project?>(null);
        }

        _Store(Current.Settings.WithUpdated(project));
        return Task.FromResult<Project?>(project);
    }

    public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt)
    {
        if (Current.Settings.Projects.FirstOrDefault(candidate => candidate.Id == projectId) is not { } stored)
        {
            return Task.FromResult(false);
        }

        _Store(Current.Settings.WithUpdated(stored with { LastOpenedAt = openedAt }));
        return Task.FromResult(true);
    }

    public Task RemoveProjectAsync(string projectId)
    {
        _Store(Current.Settings.WithoutProject(projectId));
        return Task.CompletedTask;
    }

    private void _Store(ProjectSettings settings)
    {
        Current = Current with { Settings = settings };
        Changed?.Invoke();
    }
}
