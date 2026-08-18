using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Plugins.Abstractions.Projects;

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
internal sealed class AssistantReadGateway(CockpitViewModel cockpit, ISharedProjectSourceRegistry sharedProjectSources)
    : IAssistantReadGateway, ISingletonService
{
    public Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync() =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ListSessions())
            : Dispatcher.UIThread.InvokeAsync(_ListSessions).GetTask();

    public Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync() =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ListProjects())
            : Dispatcher.UIThread.InvokeAsync(_ListProjects).GetTask();

    // The registered sources and the bound/hidden filter ids, read together on the UI thread via
    // `ProjectsViewModel.SharedProjectVisibilityFilterIds` (AC-797) — the same rule the Projects workspace
    // itself filters on, not a second copy of it. The per-source network calls then run off the UI thread.
    public async Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedProjectsAsync()
    {
        var (sources, boundIds, hiddenIds) = Dispatcher.UIThread.CheckAccess()
            ? _SharedProjectSourcesAndVisibilityFilterIds()
            : await Dispatcher.UIThread.InvokeAsync(_SharedProjectSourcesAndVisibilityFilterIds).GetTask().ConfigureAwait(false);

        if (sources.Count == 0)
        {
            return [];
        }

        var results = await Task.WhenAll(
            sources.Select(source => ProjectsViewModel._ListWithTimeoutAsync(source, CancellationToken.None)))
            .ConfigureAwait(false);

        return
        [
            .. sources.Zip(results, (source, result) => new AssistantSharedProjectSourceRow(
                source.SourceName,
                result.Succeeded,
                result.Error,
                result.Succeeded
                    ? [.. result.Projects
                        .Where(project => !boundIds.Contains(project.Id) && !hiddenIds.Contains(project.Id))
                        .Select(project => new AssistantSharedProjectRow(project.Id, project.Name, project.Description, project.Role))]
                    : [])),
        ];
    }

    private (IReadOnlyList<ISharedProjectSource> Sources, HashSet<string> BoundIds, HashSet<string> HiddenIds)
        _SharedProjectSourcesAndVisibilityFilterIds()
    {
        var (boundIds, hiddenIds) = cockpit.Projects.SharedProjectVisibilityFilterIds();
        return (sharedProjectSources.Sources, boundIds, hiddenIds);
    }

    // An SDK session's transcript is in memory and is read on the UI thread; a TTY session's is a file its CLI
    // wrote, and reading it is not something to do on the thread that draws (AC-609). So the pane is resolved on
    // the UI thread — that is where the roster lives — and only the file read is handed off.
    public async Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count)
    {
        var found = Dispatcher.UIThread.CheckAccess()
            ? _ReadTranscript(paneId, count)
            : await Dispatcher.UIThread.InvokeAsync(() => _ReadTranscript(paneId, count)).GetTask()
                .ConfigureAwait(false);

        return found switch
        {
            { Transcript: { } transcript } => transcript,
            { Tty: { } tty } => await Task.Run(() => _ReadTtyTranscript(tty, count)).ConfigureAwait(false),
            _ => null,
        };
    }

    // What the UI-thread half found: an SDK session's transcript, already read; a TTY session, whose transcript is
    // a file to be read off this thread; or neither, for a plain terminal or a pane id that names nothing.
    private readonly record struct FoundTranscript(AssistantTranscript? Transcript, TtyViewModel? Tty);

    // The last `count` rows of a session's transcript, or nothing when that pane is not an AI session.
    // *The type test is the lookup.* `CockpitViewModel.FindSession` answers in `SessionPanelViewModel`, the shared
    // base of an SDK session, a TTY session and a plain terminal. Both session kinds are real AI sessions with a
    // real transcript and both are answered here; only a terminal falls out as "no AI session", which is true of it
    // rather than a convenient approximation. It used to be the TTY panes that fell out too — and since TTY is how
    // most of this cockpit's sessions run, the answer contradicted `list_sessions`, which had just reported the same
    // pane as a live session with a current statusline (AC-609). Embedded panes are reachable for the same reason
    // `FindSession` exists: an Autopilot step is a full session with a real transcript, and a reader that only
    // walked the grid would answer confidently and wrongly about it.
    //
    // The SDK slice is taken here, on the UI thread, so a session with ten thousand rows costs a `Skip` rather than
    // a copy. Nothing is filtered on the way out — a thinking row and a folded tool call are in the transcript and
    // are therefore in the answer, whether or not the operator's current reading level draws them. What the
    // assistant is being asked is what the session *did*, not what a particular panel is showing.
    private FoundTranscript _ReadTranscript(string paneId, int count)
    {
        switch (cockpit.FindSession(paneId))
        {
            case SessionViewModel session:
                var transcript = session.Transcript;
                var skip = Math.Max(0, transcript.Count - count);
                return new FoundTranscript(
                    new AssistantTranscript(
                        session.PaneId,
                        session.Title,
                        transcript.Count,
                        [
                            .. transcript.Skip(skip).Select(entry =>
                                new AssistantTranscriptEntry(entry.Kind.ToString(), entry.TextWithImageSuffix, entry.ResultText)),
                        ]),
                    null);

            // A plain terminal is a TtyViewModel too, and has no agent behind it to have written anything. It is
            // left to fall through the same way it always did: `ReadTranscriptEntries` has no record to name for
            // it and answers empty, which the caller reports as a pane with nothing to read.
            case TtyViewModel tty when !tty.IsTerminal:
                return new FoundTranscript(null, tty);

            default:
                return default;
        }
    }

    // A TTY session's transcript, read from the file its own CLI wrote. Off the UI thread — see
    // `ReadTranscriptAsync`. An empty read is still an answer about a live session, not a missing pane: it means
    // the session has written nothing yet, or its provider cannot name its record (no statusline snapshot). The
    // caller can tell the two apart from `totalEntries`, and either way it must not report the session as gone.
    private static AssistantTranscript _ReadTtyTranscript(TtyViewModel tty, int count)
    {
        var slice = tty.ReadTranscriptEntries(count);
        return new AssistantTranscript(
            tty.PaneId,
            tty.Title,
            slice.TotalEntries,
            [.. slice.Entries.Select(entry => new AssistantTranscriptEntry(entry.Kind, entry.Text, entry.ToolResult))]);
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
                        // An SDK session has a permission to be stopped on; a TTY pane has no such state, but can
                        // reach NeedsAttention on its own route (AC-920: an unanswered `AskUserQuestion`). Kept as
                        // two arms rather than one shared `SessionStatus` check so the SDK arm stays untouched.
                        session is SessionViewModel { HasPendingPermission: true }
                            or TtyViewModel { SessionStatus: SessionStatus.NeedsAttention },
                        // The same precondition every other waker in the cockpit already checks before sending —
                        // not a second opinion computed here (AC-545 follow-up).
                        session.CanTakeAPrompt);
                }),
        ];
    }
}
