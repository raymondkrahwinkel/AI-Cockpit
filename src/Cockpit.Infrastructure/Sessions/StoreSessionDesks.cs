using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378/AC-1439: the desks without a frontend, kept in memory and saved through the store. The desktop's exclusion is
// its UI thread; this lock is the backend's. Sections are synchronous, so it is never held across an await.
public sealed class StoreSessionDesks(WorkspaceSettings workspaces, IWorkspaceSettingsStore store, ISessionRegistry registry) : ISessionDesks
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private WorkspaceSettings _workspaces = workspaces;

    public Task<T> RunExclusiveAsync<T>(Func<T> decision)
    {
        lock (_gate)
        {
            return Task.FromResult(decision());
        }
    }

    public WorkspaceSettings Workspaces
    {
        get
        {
            lock (_gate)
            {
                return _workspaces;
            }
        }
    }

    public bool CanCloseWorkspace(string workspaceId) => Workspaces.CanClose(workspaceId);

    public async Task<Workspace> CreateSessionsWorkspaceAsync(string name)
    {
        var created = Workspace.Create(name, WorkspaceType.Sessions);
        await _ApplyAsync(() => _workspaces.WithWorkspace(created)).ConfigureAwait(false);
        return created;
    }

    public Task RenameWorkspaceAsync(string workspaceId, string name) =>
        _ApplyAsync(() => string.IsNullOrWhiteSpace(name) || _Find(workspaceId) is not { } workspace
            ? _workspaces
            : _workspaces.WithUpdated(workspace with { Name = name.Trim() }));

    public async Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId)
    {
        var occupants = 0;
        await _ApplyAsync(() =>
        {
            occupants = registry.All.Count(handle => string.Equals(handle.PlacedWorkspaceId, workspaceId, StringComparison.Ordinal));
            return occupants == 0 ? _workspaces.WithoutWorkspace(workspaceId) : _workspaces;
        }).ConfigureAwait(false);
        return occupants;
    }

    public Task AddPaneAsync(string workspaceId, WorkspacePane pane) =>
        _ApplyAsync(() => _Find(workspaceId) is { } workspace ? _workspaces.WithUpdated(workspace.WithPane(pane)) : _workspaces);

    public Task RemovePaneAsync(string workspaceId, string paneId) =>
        _ApplyAsync(() => _Find(workspaceId) is { } workspace ? _workspaces.WithUpdated(workspace.WithoutPane(paneId)) : _workspaces);

    private Workspace? _Find(string workspaceId) => _workspaces.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);

    // Decided and swapped in one section; saved after it, only when something changed. One save at a time, in the
    // order the changes were made, so an older state never lands on disk after a newer one.
    private async Task _ApplyAsync(Func<WorkspaceSettings> change)
    {
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            var (before, after) = await RunExclusiveAsync(() =>
            {
                var previous = _workspaces;
                _workspaces = change();
                return (previous, _workspaces);
            }).ConfigureAwait(false);

            if (!ReferenceEquals(before, after))
            {
                await store.SaveAsync(after).ConfigureAwait(false);
            }
        }
        finally
        {
            _saving.Release();
        }
    }
}
