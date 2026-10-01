using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// AC-1439: what only the desktop has, handed to the backend's one launcher: the desks on screen, the panes that host its
// sessions, what it keeps about a project, and the assistant's window. Sessions and desks only change on the UI thread,
// so that thread is the exclusion, with the deadlines the gateway always had (AC-1375).
internal sealed class DesktopSessionSeams(Func<CockpitViewModel> cockpit)
    : ISessionDesks, ISessionHosting, ISessionStartObserver, IAssistantSessionFactory
{
    public Task<T> RunExclusiveAsync<T>(Func<T> decision) => UiThreadCall.RunAsync(decision);

    public WorkspaceSettings Workspaces => cockpit().Workspaces.Settings;

    public bool CanCloseWorkspace(string workspaceId) => cockpit().Workspaces.CanClose(workspaceId);

    public Task<Workspace> CreateSessionsWorkspaceAsync(string name) =>
        UiThreadCall.RunAsync(() => cockpit().Workspaces.CreateSessionsWorkspaceAsync(name));

    public Task RenameWorkspaceAsync(string workspaceId, string name) =>
        UiThreadCall.RunAsync(() => cockpit().Workspaces.RenameWorkspaceAsync((workspaceId, name)));

    // Counted by the same placement rule `list_workspaces` reports by, but wider than that roster: a plain terminal
    // counts too, since the close would kill its pty just the same. Nothing awaits between the count and the close.
    public Task<int> CloseWorkspaceIfEmptyAsync(string workspaceId) =>
        UiThreadCall.RunAsync(async () =>
        {
            var desktop = cockpit();
            var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(desktop.Workspaces.Settings);
            var occupants = desktop.AllSessions().Count(session => string.Equals(
                SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId), workspaceId, StringComparison.Ordinal));
            if (occupants == 0)
            {
                await desktop.CloseWorkspaceAsync(workspaceId).ConfigureAwait(true);
            }

            return occupants;
        });

    public Task AddPaneAsync(string workspaceId, WorkspacePane pane) =>
        UiThreadCall.RunAsync(() => cockpit().PersistLaunchedPaneAsync(workspaceId, pane));

    public Task RemovePaneAsync(string workspaceId, string paneId) =>
        UiThreadCall.RunAsync(() => cockpit().Workspaces.RemovePaneAsync(workspaceId, paneId));

    public bool CanHost(PaneSessionKind kind) => cockpit().CanHostSession(kind);

    public IHostedSession? Host(SessionHostingRequest request) => UiThreadCall.Run(() => cockpit().HostSession(request));

    public void SessionStarted(string paneId, string? projectId, string? projectJobId) =>
        Dispatcher.UIThread.Post(() => cockpit().NoteSessionStarted(paneId, projectId, projectJobId));

    // AC-1379: the assistant's host calls these on the UI thread, as it always called the cockpit, so they do not hop.
    public IAssistantSession? CreateAssistantSession() => cockpit().CreateAssistantSession(AssistantIdentity.PaneId);

    public void ReleaseAssistantSession(IAssistantSession session)
    {
        if (session is SessionPanelViewModel pane)
        {
            cockpit().ReleaseAssistantSession(pane);
        }
    }
}
