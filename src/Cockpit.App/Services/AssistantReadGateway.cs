using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Services;

// The app-level half of `IAssistantReadGateway` (AC-544): every session panel across every workspace, unlike
// `WorkspaceAgentGateway`'s caller-scoped desk — the assistant sits on no desk. UI-thread marshalling matches
// `CockpitViewModel.Sessions`; embedded panes count as sessions, plain terminals and the assistant's own do not.
internal sealed class AssistantReadGateway(CockpitViewModel cockpit, ISharedProjectSourceRegistry sharedProjectSources)
    : IAssistantReadGateway, ISingletonService
{
    public Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync() => UiThreadCall.RunAsync(_ListSessions);

    public Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync() => UiThreadCall.RunAsync(_ListProjects);

    // The registered sources and the bound/hidden filter ids, read together on the UI thread via
    // `ProjectsViewModel.SharedProjectVisibilityFilterIds` (AC-797) — the same rule the Projects workspace
    // itself filters on, not a second copy of it. The per-source network calls then run off the UI thread.
    public async Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedProjectsAsync()
    {
        var (sources, boundIds, hiddenIds) = await UiThreadCall
            .RunAsync(_SharedProjectSourcesAndVisibilityFilterIds).ConfigureAwait(false);

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
        var found = await UiThreadCall.RunAsync(() => _ReadTranscript(paneId, count)).ConfigureAwait(false);

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

    // The last `count` rows of a session's transcript, or nothing when that pane names no AI session — an SDK or
    // TTY session, never a plain terminal or the assistant's own. Sliced here, on the UI thread, so a session
    // with ten thousand rows costs a `Skip` rather than a copy; nothing is filtered out of what remains.
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

    // AC-1013: A TTY session's transcript, read from the file its own CLI wrote (off the UI thread, see
    // `ReadTranscriptAsync`); an empty read still answers a live session (nothing written yet, or no statusline
    // snapshot to name) rather than a missing pane, so the caller must not report it as gone.
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
            project.GitUrl,
            [.. project.SourceDirectories.Select(repository => new AssistantProjectRepositoryRow(repository.Path, repository.Label))])),
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
