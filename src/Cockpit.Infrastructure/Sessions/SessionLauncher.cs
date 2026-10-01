using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Sessions;

// AC-1378/AC-1439: the one start path, for the desktop, the assistant, the node tools and the API alike. It composes,
// admits, persists and records; `ISessionHosting` only makes the session, so a frontend's pane is its host's one owner.
public sealed class SessionLauncher(
    ISessionDesks desks,
    ISessionHosting hosting,
    SessionRegistry registry,
    SessionStartComposer composer,
    ITtySessionProviderResolver? ttyProviders = null,
    IWorktreeManager? worktrees = null,
    SessionStateRecorder? stateRecorder = null,
    ISessionStartObserver? startObserver = null) : ISessionLauncher
{
    public Task<T> RunExclusiveAsync<T>(Func<T> decision) => desks.RunExclusiveAsync(decision);

    public WorkspaceSettings Workspaces => desks.Workspaces;

    public bool CanCloseWorkspace(string workspaceId) => desks.CanCloseWorkspace(workspaceId);

    public bool ProfileHasTtyRoute(SessionProfile profile) => TtyRoute.Exists(profile, ttyProviders);

    public Task<Project?> FindProjectByIdAsync(string projectId) => composer.FindProjectAsync(projectId);

    public async Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request)
    {
        request = await composer.ComposeAsync(request).ConfigureAwait(false);
        var profile = request.Profile;
        var kind = request.Kind ?? PaneSessionKind.Sdk;
        if (!hosting.CanHost(kind))
        {
            // A host that runs no sessions at all starts nothing; one that runs them but no terminal says why.
            return kind == PaneSessionKind.Tty && hosting.CanHost(PaneSessionKind.Sdk) ? throw new TtyLaunchRefusedException(profile.Label) : null;
        }

        // AC-410: a pane a restart brought back is registered, recorded and waiting; naming it starts that one.
        var paneId = request.PaneId ?? Guid.NewGuid().ToString("n");
        var waiting = registry.Find(paneId) as IHostedSession;
        if (waiting is { AwaitsStart: false } || (waiting is null && registry.Find(paneId) is not null))
        {
            throw new InvalidOperationException($"Pane '{paneId}' is already running.");
        }

        var name = waiting?.Title ?? _NameOf(request);
        var admitted = await WorktreeAdmission.AdmitAsync(
            worktrees, paneId, profile.Label, request.WorkingDirectory, request.IsolateInWorktree == true && !request.RunUnisolated).ConfigureAwait(false);
        if (admitted.Decision is { } decision)
        {
            return new LaunchedSession(string.Empty, name, null, decision);
        }

        var hosted = waiting ?? await _HostAsync(request, paneId, name, kind, admitted).ConfigureAwait(false);
        if (hosted is null)
        {
            return null;
        }

        try
        {
            if (waiting is not null && admitted.WorktreeBranch is { } reattached)
            {
                await waiting.SetWorktreeBranchAsync(reattached).ConfigureAwait(false);
            }

            // AC-1080: a resumed conversation repaints its recorded log before the first turn.
            if (request.Resume is { } resume)
            {
                await hosted.PrepareRecordedTranscriptAsync(resume).ConfigureAwait(false);
            }

            var instructed = request with
            {
                LaunchOptions = SessionStartComposer.WithInstructions(request.LaunchOptions, request.SystemPrompt, request.ProjectJobId is not null),
            };
            await hosted.StartAsync(instructed, admitted.WorkingDirectory).ConfigureAwait(false);

            // Stopped while it came up: the stop found no runtime yet, so this one would run with no pane to reach it.
            if (registry.Find(paneId) != hosted)
            {
                await _TearDownAsync(hosted).ConfigureAwait(false);
                return null;
            }
        }
        catch when (waiting is null)
        {
            // A session that never came up goes, record and all, and the reason travels to the caller. A restored pane
            // stays, as it always did, and can be offered again.
            registry.Unregister(paneId);
            await _TearDownAsync(hosted).ConfigureAwait(false);
            await desks.RemovePaneAsync(request.WorkspaceId, paneId).ConfigureAwait(false);
            throw;
        }

        await _RecordStartAsync(hosted, request, kind, admitted).ConfigureAwait(false);

        bool? promptDelivered = string.IsNullOrWhiteSpace(request.Prompt)
            ? null
            : await hosted.SubmitPromptWhenReadyAsync(request.Prompt).ConfigureAwait(false);
        return new LaunchedSession(paneId, name, promptDelivered);
    }

    // Hosted, registered and recorded, in that order; null when the desk stopped holding sessions meanwhile.
    private async Task<IHostedSession?> _HostAsync(SessionLaunchRequest request, string paneId, string name, PaneSessionKind kind, AdmittedDirectory admitted)
    {
        // AC-1332: the handle carries who asked for it, so the pane it lands as is stamped before anything lists it.
        var nameIsChosen = !request.NameIsComposed && !string.IsNullOrWhiteSpace(request.SessionName);
        IHostedSession? hosted = null;
        bool registered;
        try
        {
            hosted = hosting.Host(new SessionHostingRequest(paneId, name, nameIsChosen, kind, request, admitted.WorkingDirectory, admitted.WorktreeBranch));

            // Registered in the same section that checks the desk, so a close counting its sessions sees this one.
            registered = hosted is not null
                && await RunExclusiveAsync(() => _IsSessionsDesk(request.WorkspaceId) && registry.Find(paneId) is null && _Register(hosted)).ConfigureAwait(false);
        }
        catch
        {
            // A UI that did not answer in time: the worktree admitted for this pane and whatever was made go with it.
            await _AbandonAsync(paneId, hosted).ConfigureAwait(false);
            throw;
        }

        if (hosted is null || !registered)
        {
            await _AbandonAsync(paneId, hosted).ConfigureAwait(false);
            return null;
        }

        // AC-410: written before the session starts, never after: a crash in between leaves at most a pane that does not
        // come back, never one that comes back describing a session that never started this way.
        try
        {
            await desks.AddPaneAsync(request.WorkspaceId, new WorkspacePane(paneId, PaneKind.AiSession)
            {
                ProfileId = request.Profile.Label,
                SessionKind = kind,
                WorkingDirectory = request.WorkingDirectory,
                Title = name,
                NameIsChosen = nameIsChosen,
                ProjectId = request.ProjectId,
                StartedByTheAssistant = request.StartedByTheAssistant,
            }).ConfigureAwait(false);
        }
        catch
        {
            await _AbandonAsync(paneId, hosted).ConfigureAwait(false);
            throw;
        }

        return hosted;
    }

    // Unregistered only while it is still this one, then stopped, with its admitted worktree released.
    private async Task _AbandonAsync(string paneId, IHostedSession? hosted)
    {
        if (hosted is not null && registry.Find(paneId) == hosted)
        {
            registry.Unregister(paneId);
        }

        if (hosted is not null)
        {
            await _TearDownAsync(hosted).ConfigureAwait(false);
        }
        else if (worktrees is not null)
        {
            await _ReleaseWorktreeAsync(paneId).ConfigureAwait(false);
        }
    }

    public async Task StopSessionAsync(string paneId)
    {
        if (registry.Find(paneId) is not IHostedSession { IsEmbedded: false } hosted)
        {
            return;
        }

        // A frontend's pane closes itself, record and worktree included; a backend host is the launcher's to clear.
        if (hosted is not SessionHostHandle)
        {
            await hosting.StopAsync(hosted).ConfigureAwait(false);
            return;
        }

        registry.Unregister(paneId);
        await _TearDownAsync(hosted).ConfigureAwait(false);
        await desks.RemovePaneAsync(hosted.WorkspaceId, paneId).ConfigureAwait(false);
    }

    public async Task<bool> SetSessionNameAsync(string paneId, string name) =>
        !string.IsNullOrWhiteSpace(name)
        && registry.Find(paneId) is { IsEmbedded: false } handle
        && await handle.SetNameAsync(name).ConfigureAwait(false);

    public Task<Workspace> CreateSessionsWorkspaceAsync(string name) => desks.CreateSessionsWorkspaceAsync(name);

    public Task RenameWorkspaceAsync(string workspaceId, string name) => desks.RenameWorkspaceAsync(workspaceId, name);

    public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) => desks.CloseWorkspaceIfEmptyAsync(workspaceId);

    // A second session on the same project is "Cockpit 2", not a second "Cockpit" (AC-324). Only a composed name is
    // numbered: a name somebody typed starts exactly as typed.
    private string _NameOf(SessionLaunchRequest request)
    {
        var given = request.SessionName?.Trim();
        var name = string.IsNullOrEmpty(given) ? request.Profile.Label : given;
        if (!request.NameIsComposed && !string.IsNullOrEmpty(given))
        {
            return name;
        }

        var taken = registry.All.Where(handle => !handle.IsEmbedded).Select(handle => handle.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (var number = 2; taken.Contains(unique); number++)
        {
            unique = $"{name} {number}";
        }

        return unique;
    }

    // Written once, after the start: which worktree this session owns (AC-409), then the badge for a folder that is a
    // cockpit worktree it does not own (AC-633), then what the frontend keeps about the project (AC-490).
    private async Task _RecordStartAsync(IHostedSession hosted, SessionLaunchRequest request, PaneSessionKind kind, AdmittedDirectory admitted)
    {
        var permissionMode = kind == PaneSessionKind.Sdk || request.Profile.Provider is SessionProvider.ClaudeCli
            ? request.PermissionMode ?? SessionPermissionModes.Default
            : null;
        _ = stateRecorder?.RecordSessionStartedAsync(
            hosted.PaneId, request.Profile, admitted.WorkingDirectory,
            worktreePath: admitted.WorktreeBranch is not null ? admitted.WorkingDirectory : null,
            worktreeBranch: admitted.WorktreeBranch,
            permissionMode);

        if (admitted.WorktreeBranch is null && admitted.WorkingDirectory is { Length: > 0 } directory)
        {
            try
            {
                if ((await WorktreeAdmission.MatchingAsync(worktrees, directory).ConfigureAwait(false))?.Branch is { } branch)
                {
                    await hosted.SetWorktreeBranchAsync(branch).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A badge is never worth a started session: an unreadable registry or a path git rejects leaves it off.
            }
        }

        startObserver?.SessionStarted(hosted.PaneId, request.ProjectId, request.ProjectJobId);
    }

    // Keyed on the pane, as the desktop's close is: a clean worktree goes with its branch, one holding work is retained.
    // Released here too for a pane that never landed, which no close will ever reach.
    private async Task _TearDownAsync(IHostedSession hosted)
    {
        await hosting.StopAsync(hosted).ConfigureAwait(false);
        await _ReleaseWorktreeAsync(hosted.PaneId).ConfigureAwait(false);
    }

    private async Task _ReleaseWorktreeAsync(string paneId)
    {
        if (worktrees is null)
        {
            return;
        }

        try
        {
            await worktrees.ReleaseAsync(paneId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Left for the startup reconcile, as on the desktop.
        }
    }

    private bool _IsSessionsDesk(string workspaceId) =>
        desks.Workspaces.Workspaces.Any(workspace => workspace.Id == workspaceId && workspace.Type == WorkspaceType.Sessions);

    private bool _Register(ISessionHandle handle)
    {
        registry.Register(handle);
        return true;
    }
}
