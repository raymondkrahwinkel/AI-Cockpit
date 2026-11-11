using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Sessions;

// The save gate also excludes session admission, so a close cannot race a start onto the desk being removed.
public sealed class StoreSessionDesks(WorkspaceSettings workspaces, IWorkspaceSettingsStore store, ISessionRegistry registry) : ISessionDesks, IWorkspaceCommands
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _saving = new(1, 1);
    private WorkspaceSettings _workspaces = workspaces;

    public event EventHandler? Changed;

    public async Task<T> RunExclusiveAsync<T>(Func<T> decision)
    {
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                return decision();
            }
        }
        finally
        {
            _saving.Release();
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
        return await CreateWorkspaceAsync(Workspace.Create(name, WorkspaceType.Sessions)).ConfigureAwait(false);
    }

    public async Task<Workspace> CreateWorkspaceAsync(Workspace workspace)
    {
        var created = workspace with { Id = Guid.NewGuid().ToString("n"), Name = workspace.Name.Trim(), Panes = [], IsFloating = false };
        await _ApplyAsync(() => _workspaces.WithWorkspace(created)).ConfigureAwait(false);
        return created;
    }

    public Task SelectWorkspaceAsync(string workspaceId) => _ApplyAsync(() => _workspaces.WithActive(workspaceId));

    public Task MoveWorkspaceAsync(string workspaceId, int index) => _ApplyAsync(() => _workspaces.WithMoved(workspaceId, index));

    public Task UpdateWorkspaceAsync(Workspace workspace) => _ApplyAsync(() =>
        _Find(workspace.Id) is not { } current ? _workspaces : _workspaces.WithUpdated(current.WithPresentation(workspace)));

    public async Task<bool> MoveSessionPaneAsync(string paneId, string targetWorkspaceId)
    {
        var moved = false;
        await _ApplyAsync(() =>
        {
            var source = _workspaces.Workspaces.FirstOrDefault(workspace => workspace.Panes.Any(pane => pane.Id == paneId));
            if (source is null || _Find(targetWorkspaceId) is not { Type: var type } target || type != WorkspaceType.Sessions
                || registry.Find(paneId) is not SessionHostHandle)
            {
                return _workspaces;
            }
            var pane = source.Panes.First(pane => pane.Id == paneId);
            moved = true;
            return source.Id == target.Id ? _workspaces : _workspaces
                .WithUpdated(source.WithoutPane(paneId)).WithUpdated(target.WithPane(pane));
        }, () =>
        {
            if (moved && registry.Find(paneId) is SessionHostHandle handle)
            {
                handle.MoveToWorkspace(targetWorkspaceId);
            }
        }).ConfigureAwait(false);
        return moved;
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
            occupants = Math.Max(_Find(workspaceId)?.Panes.Count(pane => pane.Kind != PaneKind.Widget) ?? 0,
                registry.All.Count(handle => string.Equals(handle.PlacedWorkspaceId, workspaceId, StringComparison.Ordinal)));
            return occupants == 0 ? _workspaces.WithoutWorkspace(workspaceId) : _workspaces;
        }).ConfigureAwait(false);
        return occupants;
    }

    public Task AddPaneAsync(string workspaceId, WorkspacePane pane) =>
        _ApplyAsync(() => _Find(workspaceId) is { } workspace ? _workspaces.WithUpdated(workspace.WithPane(pane)) : _workspaces);

    public Task RemovePaneAsync(string workspaceId, string paneId) =>
        _ApplyAsync(() => _Find(workspaceId) is { } workspace ? _workspaces.WithUpdated(workspace.WithoutPane(paneId)) : _workspaces);

    internal Task RenamePaneAsync(string workspaceId, string paneId, string title) =>
        _ApplyAsync(() => _Find(workspaceId) is { } workspace ? _workspaces.WithUpdated(workspace.WithPaneRenamed(paneId, title, true)) : _workspaces);

    private Workspace? _Find(string workspaceId) => _workspaces.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId);

    // Decided and swapped in one section; saved after it, only when something changed. One save at a time, in the
    // order the changes were made, so an older state never lands on disk after a newer one.
    private async Task _ApplyAsync(Func<WorkspaceSettings> change, Action? committed = null)
    {
        await _saving.WaitAsync().ConfigureAwait(false);
        try
        {
            WorkspaceSettings before;
            WorkspaceSettings after;
            lock (_gate)
            {
                before = _workspaces;
                _workspaces = change();
                after = _workspaces;
            }

            if (!ReferenceEquals(before, after))
            {
                try
                {
                    await store.SaveAsync(after).ConfigureAwait(false);
                }
                catch
                {
                    lock (_gate)
                    {
                        _workspaces = before;
                    }
                    throw;
                }
            }
            committed?.Invoke();
            if (!ReferenceEquals(before, after))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _saving.Release();
        }
    }
}
