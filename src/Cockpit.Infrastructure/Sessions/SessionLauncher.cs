using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: `ISessionLauncher` without a frontend. It starts SDK sessions through `SessionHost.StartAsync`, the start the
// desktop pane runs too, and refuses a TTY one with the reason; the desktop keeps its own launcher until F4 (AC-1372).
// Not registered in the container yet: the backend bootstrap (F1.9) builds it over the desks it loaded.
public sealed class SessionLauncher(
    WorkspaceSettings workspaces,
    IWorkspaceSettingsStore workspaceStore,
    IProjectStore projectStore,
    SessionRegistry registry,
    ISessionManager sessionManager,
    TimeProvider time,
    ITtySessionProviderResolver? ttyProviders = null,
    ISessionTranscriptStore? transcriptStore = null) : ISessionLauncher
{
    // The desktop's exclusion is its UI thread; this is the backend's. Sections are synchronous, so it is never held
    // across an await.
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

    public bool ProfileHasTtyRoute(SessionProfile profile) => TtyRoute.Exists(profile, ttyProviders);

    public async Task<Project?> FindProjectByIdAsync(string projectId) =>
        (await projectStore.LoadAsync().ConfigureAwait(false)).Projects.FirstOrDefault(project => project.Id == projectId);

    public async Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request)
    {
        var profile = request.Profile;
        if (request.Kind == PaneSessionKind.Tty || (request.Kind is null && TtyRoute.IsDefault(profile, ttyProviders)))
        {
            throw new TtyLaunchRefusedException(profile.Label);
        }

        // The worktree step is the desktop's (`CockpitViewModel._ResolveIsolatedWorkingDirectoryAsync`); running in the
        // shared checkout instead of the isolation that was asked for would be the one outcome isolation exists to stop.
        if (request.IsolateInWorktree == true)
        {
            throw new InvalidOperationException(
                "This cockpit cannot isolate a session in a worktree of its own yet, so it did not start one in the shared checkout either.");
        }

        var paneId = Guid.NewGuid().ToString("n");
        var name = string.IsNullOrWhiteSpace(request.SessionName) ? $"{profile.Label} — {DateTime.Now:HH:mm}" : request.SessionName.Trim();
        var host = new SessionHost<QueuedPrompt>(() => paneId, sessionManager, time, transcriptStore: transcriptStore);
        var handle = new SessionHostHandle(
            paneId, name, !string.IsNullOrWhiteSpace(request.SessionName), request.WorkspaceId, request.WorkingDirectory, profile.Label, host);

        // Registered in the same section that checks the desk, so a close counting its sessions sees this one.
        if (!await RunExclusiveAsync(() => _IsSessionsDesk(request.WorkspaceId) && _Register(handle)).ConfigureAwait(false))
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        try
        {
            var runtime = await host.StartAsync(new SessionStart(
                profile, PermissionMode: null, Model: null,
                McpServerRegistryFilter.EffectiveSessionSelection(null, profile.EnabledMcpServerNames),
                request.WorkingDirectory, SessionResume.New, request.LaunchOptions, request.ProjectId)).ConfigureAwait(false);
            if (runtime is not { IsRunning: true })
            {
                throw new InvalidOperationException("The provider returned without a running session.");
            }

            // Stopped while it came up: the stop found no runtime yet, so this one would run with no pane to reach it.
            if (registry.Find(paneId) != handle)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }
        catch
        {
            // A pane that never came up has no view to say so on: it goes, and the reason travels to the caller.
            registry.Unregister(paneId);
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        bool? promptDelivered = string.IsNullOrWhiteSpace(request.Prompt)
            ? null
            : await handle.SubmitPromptWhenReadyAsync(request.Prompt).ConfigureAwait(false);
        return new LaunchedSession(paneId, name, promptDelivered);
    }

    public async Task StopSessionAsync(string paneId)
    {
        if (registry.Find(paneId) is not SessionHostHandle handle)
        {
            return;
        }

        registry.Unregister(paneId);
        await handle.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<bool> SetSessionNameAsync(string paneId, string name) =>
        registry.Find(paneId) is SessionHostHandle handle && await handle.SuggestNameAsync(name).ConfigureAwait(false);

    public async Task<Workspace> CreateSessionsWorkspaceAsync(string name)
    {
        var created = Workspace.Create(name, WorkspaceType.Sessions);
        await _ApplyAsync(() => _workspaces.WithWorkspace(created)).ConfigureAwait(false);
        return created;
    }

    public Task RenameWorkspaceAsync(string workspaceId, string name) =>
        _ApplyAsync(() => string.IsNullOrWhiteSpace(name)
            || _workspaces.Workspaces.FirstOrDefault(workspace => workspace.Id == workspaceId) is not { } workspace
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

    private bool _IsSessionsDesk(string workspaceId) =>
        _workspaces.Workspaces.Any(workspace => workspace.Id == workspaceId && workspace.Type == WorkspaceType.Sessions);

    private bool _Register(ISessionHandle handle)
    {
        registry.Register(handle);
        return true;
    }

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
                await workspaceStore.SaveAsync(after).ConfigureAwait(false);
            }
        }
        finally
        {
            _saving.Release();
        }
    }
}
