using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// AC-1375: `ISessionLauncher` over the cockpit's own start and desk doors. Sessions and desks only change on the UI
// thread, so that thread is the exclusion: every member takes the `UiThreadCall` route the gateway took before it
// moved, with the same deadlines, and runs inline when a caller is already there.
internal sealed class SessionLauncherAdapter(CockpitViewModel cockpit) : ISessionLauncher, ISingletonService
{
    public Task<T> RunExclusiveAsync<T>(Func<T> decision) => UiThreadCall.RunAsync(decision);

    public WorkspaceSettings Workspaces => cockpit.Workspaces.Settings;

    public bool CanCloseWorkspace(string workspaceId) => cockpit.Workspaces.CanClose(workspaceId);

    public bool ProfileHasTtyRoute(SessionProfile profile) => cockpit.ProfileHasTtyRoute(profile);

    public Task<Project?> FindProjectByIdAsync(string projectId) => UiThreadCall.RunAsync(() => cockpit.FindProjectByIdAsync(projectId));

    public Task<LaunchedSession?> StartSessionAsync(SessionLaunchRequest request) =>
        UiThreadCall.RunAsync(async () =>
            await cockpit.StartSessionOnWorkspaceAsync(
                request.WorkspaceId, request.Profile, request.Prompt, request.WorkingDirectory, request.SessionName,
                request.Kind switch
                {
                    PaneSessionKind.Sdk => SessionKind.Sdk,
                    PaneSessionKind.Tty => SessionKind.Tty,
                    _ => null,
                },
                request.LaunchOptions, request.IsolateInWorktree, explicitProjectId: request.ProjectId,
                startedByTheAssistant: request.StartedByTheAssistant).ConfigureAwait(true) is { } started
                ? new LaunchedSession(started.PaneId, started.Name, started.PromptDelivered)
                : null);

    // Sessions only, never FindSession: CloseSessionAsync no-ops for an embedded pane, and the gateway has already
    // refused those with a reason of their own.
    // AC-1379: the assistant's host calls these on the UI thread, as it always called the cockpit, so they do not hop.
    public IAssistantSession? CreateAssistantSession() => cockpit.CreateAssistantSession(AssistantIdentity.PaneId);

    public void ReleaseAssistantSession(IAssistantSession session)
    {
        if (session is SessionPanelViewModel pane)
        {
            cockpit.ReleaseAssistantSession(pane);
        }
    }

    public Task StopSessionAsync(string paneId) =>
        UiThreadCall.RunAsync(() =>
            cockpit.Sessions.FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal)) is { } session
                ? cockpit.StopSessionForAssistantAsync(session)
                : Task.CompletedTask);

    public Task<bool> SetSessionNameAsync(string paneId, string name) => UiThreadCall.RunAsync(() => cockpit.SetSessionName(paneId, name));

    public Task<Workspace> CreateSessionsWorkspaceAsync(string name) =>
        UiThreadCall.RunAsync(() => cockpit.Workspaces.CreateSessionsWorkspaceAsync(name));

    public Task RenameWorkspaceAsync(string workspaceId, string name) =>
        UiThreadCall.RunAsync(() => cockpit.Workspaces.RenameWorkspaceAsync((workspaceId, name)));

    // Counted by the same placement rule `list_workspaces` reports by, but wider than that roster: a plain terminal
    // counts too, since the close would kill its pty just the same. Nothing awaits between the count and the close.
    public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) =>
        UiThreadCall.RunAsync(async () =>
        {
            var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);
            var occupants = cockpit.AllSessions().Count(session => string.Equals(
                SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId), workspaceId, StringComparison.Ordinal));
            if (occupants == 0)
            {
                await cockpit.CloseWorkspaceAsync(workspaceId).ConfigureAwait(true);
            }

            return occupants;
        });
}
