using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Assistant;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: `ISessionLauncher` without a frontend. It starts SDK sessions through `SessionHost.StartAsync`, the start the
// desktop pane runs too, and refuses a TTY one with the reason; the desktop keeps its own launcher until F4 (AC-1372).
// Registered by the backend bootstrap (AC-1381) over the saved desks; worktree admission is the desktop's rule (AC-1448).
public sealed class SessionLauncher(
    WorkspaceSettings workspaces,
    IWorkspaceSettingsStore workspaceStore,
    IProjectStore projectStore,
    SessionRegistry registry,
    ISessionManager sessionManager,
    TimeProvider time,
    ITtySessionProviderResolver? ttyProviders = null,
    ISessionTranscriptStore? transcriptStore = null,
    IWorktreeManager? worktrees = null) : ISessionLauncher
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

        var paneId = request.PaneId ?? Guid.NewGuid().ToString("n");
        if (registry.Find(paneId) is not null)
        {
            throw new InvalidOperationException($"Pane '{paneId}' is already running.");
        }

        var name = string.IsNullOrWhiteSpace(request.SessionName) ? $"{profile.Label} — {DateTime.Now:HH:mm}" : request.SessionName.Trim();
        var admitted = await WorktreeAdmission.AdmitAsync(
            worktrees, paneId, profile.Label, request.WorkingDirectory, request.IsolateInWorktree == true).ConfigureAwait(false);
        if (admitted.Decision is { } decision)
        {
            return new LaunchedSession(string.Empty, name, null, decision);
        }

        var host = new SessionHost(() => paneId, sessionManager, time, transcriptStore: transcriptStore);
        var handle = new SessionHostHandle(
            paneId, name, !string.IsNullOrWhiteSpace(request.SessionName), request.WorkspaceId, admitted.WorkingDirectory, profile.Label, host, request.ProjectId);
        await handle.SetWorktreeBranchAsync(admitted.WorktreeBranch).ConfigureAwait(false);

        // Registered in the same section that checks the desk, so a close counting its sessions sees this one.
        if (!await RunExclusiveAsync(() => _IsSessionsDesk(request.WorkspaceId) && registry.Find(paneId) is null && _Register(handle)).ConfigureAwait(false))
        {
            await _TearDownAsync(handle).ConfigureAwait(false);
            return null;
        }

        try
        {
            // AC-1080: a resumed conversation repaints its recorded log before the first turn.
            var resume = request.Resume ?? SessionResume.New;
            if (request.Resume is not null)
            {
                await handle.PrepareRecordedTranscriptAsync(resume).ConfigureAwait(false);
            }

            var runtime = await host.StartAsync(new SessionStart(
                profile, request.PermissionMode, request.Model,
                McpServerRegistryFilter.EffectiveSessionSelection(null, profile.EnabledMcpServerNames),
                admitted.WorkingDirectory, resume, request.LaunchOptions, request.ProjectId)).ConfigureAwait(false);
            if (runtime is not { IsRunning: true })
            {
                throw new InvalidOperationException("The provider returned without a running session.");
            }

            // Stopped while it came up: the stop found no runtime yet, so this one would run with no pane to reach it.
            if (registry.Find(paneId) != handle)
            {
                await _TearDownAsync(handle).ConfigureAwait(false);
                return null;
            }
        }
        catch
        {
            // A pane that never came up has no view to say so on: it goes, and the reason travels to the caller.
            registry.Unregister(paneId);
            await _TearDownAsync(handle).ConfigureAwait(false);
            throw;
        }

        bool? promptDelivered = string.IsNullOrWhiteSpace(request.Prompt)
            ? null
            : await handle.SubmitPromptWhenReadyAsync(request.Prompt).ConfigureAwait(false);
        return new LaunchedSession(paneId, name, promptDelivered);
    }

    // AC-1379: the assistant on no desk, as the registry's `Assistant`, never in `All`. Its host starts it and holds
    // the only reference; nothing here stops it, since only that host ever ends it.
    public IAssistantSession? CreateAssistantSession()
    {
        var host = new SessionHost(() => AssistantIdentity.PaneId, sessionManager, time, transcriptStore: transcriptStore);
        var handle = new SessionHostHandle(
            AssistantIdentity.PaneId, AssistantProfileSlot.DisplayName, nameIsChosen: true,
            Workspaces.Workspaces.FirstOrDefault(workspace => workspace.Type == WorkspaceType.Sessions)?.Id ?? string.Empty,
            CockpitBuild.StateRoot, AssistantProfileSlot.DisplayName, host);
        registry.RegisterAssistant(handle);
        return handle;
    }

    // Forgotten only while it is still the one held, as `CockpitViewModel.ReleaseAssistantSession` does on the desktop.
    public void ReleaseAssistantSession(IAssistantSession session)
    {
        if (ReferenceEquals(registry.Assistant, session))
        {
            registry.UnregisterAssistant();
        }
    }

    public async Task StopSessionAsync(string paneId)
    {
        if (registry.Find(paneId) is not SessionHostHandle handle)
        {
            return;
        }

        registry.Unregister(paneId);
        await _TearDownAsync(handle).ConfigureAwait(false);
    }

    // Keyed on the pane, as the desktop's close is: a clean worktree goes with its branch, one holding work is retained.
    private async Task _TearDownAsync(SessionHostHandle handle)
    {
        await handle.DisposeAsync().ConfigureAwait(false);
        if (worktrees is null)
        {
            return;
        }

        try
        {
            await worktrees.ReleaseAsync(handle.PaneId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Left for the startup reconcile, as on the desktop.
        }
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
