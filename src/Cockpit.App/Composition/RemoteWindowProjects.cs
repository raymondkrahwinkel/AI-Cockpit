using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.Composition;

// AC-2281: the remote window's Projects page over its one server's projects. The server object arrives after the window
// is built and is replaced when its row changes, so every call asks for the current one, as RemoteOptionsFor does.
internal sealed class RemoteWindowProjects(Func<IServerProjects?> current) : IProjectCatalog, IProjectEditor
{
    private const string NotConnected = "The server is not connected yet.";

    private IServerProjects? _followed;

    public ProjectCatalogSnapshot Current => _Follow()?.Current ?? ProjectCatalogSnapshot.Empty;

    public event Action? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => _Follow()?.LoadAsync(cancellationToken) ?? Task.CompletedTask;

    public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SyncNowAsync(string projectId) => _Required().SyncNowAsync(projectId);

    public Task HideSharedProjectAsync(string sharedProjectId) => _Required().HideSharedProjectAsync(sharedProjectId);

    public Task UnhideSharedProjectsAsync(string sourceKey, string organizationSlug) => _Required().UnhideSharedProjectsAsync(sourceKey, organizationSlug);

    public Task<Project?> FindProjectAsync(string projectId) => _Follow()?.FindProjectAsync(projectId) ?? Task.FromResult<Project?>(null);

    public Task<Project> AddNewProjectAsync(Project project) => _Required().AddNewProjectAsync(project);

    public Task<Project> AddBoundProjectAsync(Project project, string? baselineChecksum) => _Required().AddBoundProjectAsync(project, baselineChecksum);

    public Task<Project?> UpdateStoredProjectAsync(Project project) => _Required().UpdateStoredProjectAsync(project);

    // The server records its own sessions' starts; this window has nothing to write.
    public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt) => Task.FromResult(false);

    public Task RemoveProjectAsync(string projectId) => _Required().RemoveProjectAsync(projectId);

    private IServerProjects _Required() => _Follow() ?? throw new InvalidOperationException(NotConnected);

    // Moves the Changed subscription to the server object that is current now.
    private IServerProjects? _Follow()
    {
        var projects = current();
        if (!ReferenceEquals(projects, _followed))
        {
            if (_followed is not null)
            {
                _followed.Changed -= _OnChanged;
            }

            _followed = projects;
            if (projects is not null)
            {
                projects.Changed += _OnChanged;
            }
        }

        return projects;
    }

    private void _OnChanged() => Changed?.Invoke();
}
