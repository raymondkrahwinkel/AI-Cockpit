using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.App.Services;

// The app-level half of `IAssistantReadGateway` (AC-544): the running session panels, read across
// every workspace at once.
// Deliberately close in shape to `WorkspaceAgentGateway` and deliberately not the same class. That one
// answers "who shares this caller's desk" and derives the desk from the caller; this one answers "what is running
// anywhere" and has no caller to derive anything from — the assistant sits on no desk. Folding the second question
// into the first would have meant giving that gateway a mode in which it does not scope, and a scoping rule with
// an off switch is the thing AC-544 exists to avoid handing out.
//
// The UI-thread marshalling is the same and for the same reason: `CockpitViewModel.Sessions` only ever mutates
// on the UI thread and an MCP tool call arrives on a Kestrel request thread. Inline when already there, so a unit
// test on the UI thread pays for no redundant dispatch, and the awaitable is handed back rather than blocked on.
//
// *Every session, including the ones the grid does not show.* `CockpitViewModel.AllSessions` holds
// embedded panes (an Autopilot step, a plugin run) as well as the ones in the layout — they are full agent sessions
// with their own MCP tokens, and a status question that skipped them would be answered confidently and wrongly.
// Plain terminal panes are left out: they carry a pane id but there is no agent on the other end to have a status.
// The assistant's own session is left out too — it is the one asking.
internal sealed class AssistantReadGateway(CockpitViewModel cockpit) : IAssistantReadGateway, ISingletonService
{
    public Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync() =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ListSessions())
            : Dispatcher.UIThread.InvokeAsync(_ListSessions).GetTask();

    public Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync() =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ListProjects())
            : Dispatcher.UIThread.InvokeAsync(_ListProjects).GetTask();

    public Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count) =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ReadTranscript(paneId, count))
            : Dispatcher.UIThread.InvokeAsync(() => _ReadTranscript(paneId, count)).GetTask();

    // The last `count` rows of a session's transcript, or null when that pane is not an AI session.
    // *The type test is the lookup.* `CockpitViewModel.FindSession` answers in
    // `SessionPanelViewModel`, which is the shared base of an SDK session and a plain terminal — and only
    // the former, `SessionViewModel`, has a transcript at all. So a pane id naming a terminal falls out
    // here as "no AI session", which is true of it rather than a convenient approximation. Embedded panes are
    // reachable for the same reason `FindSession` exists: an Autopilot step is a full session with a real
    // transcript, and a reader that only walked the grid would answer confidently and wrongly about it.
    //
    // The slice is taken here, on the UI thread, so a session with ten thousand rows costs a `Skip` rather than
    // a copy. Nothing is filtered on the way out — a thinking row and a folded tool call are in the transcript and
    // are therefore in the answer, whether or not the operator's current reading level draws them. What the
    // assistant is being asked is what the session *did*, not what a particular panel is showing.
    private AssistantTranscript? _ReadTranscript(string paneId, int count)
    {
        if (cockpit.FindSession(paneId) is not SessionViewModel session)
        {
            return null;
        }

        var transcript = session.Transcript;
        var skip = Math.Max(0, transcript.Count - count);
        return new AssistantTranscript(
            session.PaneId,
            session.Title,
            transcript.Count,
            [
                .. transcript.Skip(skip).Select(entry =>
                    new AssistantTranscriptEntry(entry.Kind.ToString(), entry.Text, entry.ResultText)),
            ]);
    }

    // The operator's own project list, in the order the manager holds it. All of them, including the ones with no
    // folder: an administrative project is a project, and a reader that quietly dropped those would answer "which
    // projects do we have" with a subset and no sign that it had.
    private IReadOnlyList<AssistantProjectRow> _ListProjects() =>
    [
        .. cockpit.Projects.Projects.Select(project => new AssistantProjectRow(
            project.Id,
            project.Name,
            project.Description,
            project.SourceDirectory,
            project.DefaultProfileLabel,
            project.PluginFields,
            project.GitUrl)),
    ];

    private IReadOnlyList<AssistantSessionRow> _ListSessions()
    {
        // Resolved once for the whole sweep rather than per row: the workspace label is a lookup into the same
        // settings list every time, and the fallback desk for an unassigned session is a scan of it.
        var firstSessionsWorkspaceId = SessionWorkspacePlacement.FirstSessionsWorkspaceId(cockpit.Workspaces.Settings);
        var namesById = cockpit.Workspaces.Settings.Workspaces.ToDictionary(
            workspace => workspace.Id, workspace => workspace.Name, StringComparer.Ordinal);

        return
        [
            .. cockpit.AllSessions()
                .Where(session => session.ShowPluginHeaderItems
                    && !string.Equals(session.PaneId, Core.Assistant.AssistantIdentity.PaneId, StringComparison.Ordinal))
                .Select(session =>
                {
                    // The same placement rule every other consumer uses (AC-543), not a fourth copy of it. A session
                    // it places nowhere is reported with a null workspace rather than dropped: it is running, it has
                    // a status, and leaving it out would be the silent half-answer this ticket is about.
                    var workspaceId = SessionWorkspacePlacement.Resolve(session, firstSessionsWorkspaceId);
                    return new AssistantSessionRow(
                        session.PaneId,
                        session.Title,
                        session.ActiveProfileLabel ?? string.Empty,
                        session.Statusline,
                        workspaceId,
                        workspaceId is not null && namesById.TryGetValue(workspaceId, out var name) ? name : null,
                        session.SessionStatus.ToString(),
                        // Only an SDK session has a permission to be stopped on; a terminal pane has no such state,
                        // and reporting false for it is the truth rather than a gap.
                        session is SessionViewModel { HasPendingPermission: true },
                        // The same precondition every other waker in the cockpit already checks before sending —
                        // not a second opinion computed here (AC-545 follow-up).
                        session.CanTakeAPrompt);
                }),
        ];
    }
}
