using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Infrastructure.Projects;

namespace Cockpit.Infrastructure.Assistant;

// AC-1374: moved from Cockpit.App.Services — reads through the session registry (AC-1373) rather than
// CockpitViewModel.Sessions; the registry already excludes the assistant from `All`, so the pane-id exclusion the
// App version needed is gone. UI-thread marshalling moved into the handle, PlacedWorkspaceId included.
internal sealed class AssistantReadGateway(
    ISessionRegistry sessions,
    ISharedProjectSourceRegistry sharedProjectSources,
    IProjectStore projectStore,
    IWorkspaceSettingsStore workspaceStore)
    : IAssistantReadGateway, ISingletonService
{
    public async Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync()
    {
        var panes = sessions.All.Where(session => !session.IsTerminal).ToList();
        if (panes.Count == 0)
        {
            return [];
        }

        var workspaces = await workspaceStore.LoadAsync().ConfigureAwait(false);
        var namesById = workspaces.Workspaces.ToDictionary(workspace => workspace.Id, workspace => workspace.Name, StringComparer.Ordinal);

        return await Task.WhenAll(panes.Select(session => _RowAsync(session, namesById))).ConfigureAwait(false);
    }

    private static async Task<AssistantSessionRow> _RowAsync(ISessionHandle session, IReadOnlyDictionary<string, string> namesById)
    {
        // AC-1309: what this session still carries that Status does not hold it on — a backgrounded shell,
        // tracked but deliberately not status-pinning (AC-276).
        var hasOutstandingWork = await session.HasOutstandingBackgroundShellsAsync().ConfigureAwait(false);
        var workspaceId = session.PlacedWorkspaceId;

        return new AssistantSessionRow(
            session.PaneId,
            session.Title,
            session.ActiveProfileLabel ?? string.Empty,
            session.Statusline,
            workspaceId,
            workspaceId is not null && namesById.TryGetValue(workspaceId, out var name) ? name : null,
            session.SessionStatus.ToString(),
            // Derived from Status alone (AC-1309), not computed a second time: NeedsYou meant "what is this
            // session doing", and Status already answers that — a separate `HasPendingPermission` reading here
            // was a second opinion on the same question.
            session.SessionStatus == SessionStatus.NeedsAttention,
            // The same precondition every other waker in the cockpit already checks before sending — not a
            // second opinion computed here (AC-545 follow-up).
            session.CanTakeAPrompt,
            hasOutstandingWork,
            // AC-1096: read off the same sample the sidebar row shows, so the spoken answer and the screen
            // cannot disagree about what a session is still holding.
            session.ProcessCount,
            session.ProcessCpuPercent,
            session.ProcessMemoryBytes,
            session.AbandonedProcessCount);
    }

    public async Task<IReadOnlyList<AssistantProjectRow>> ListProjectsAsync()
    {
        var settings = await projectStore.LoadAsync().ConfigureAwait(false);
        return
        [
            // The operator's own project list, in the order the store holds it. All of them, including the ones
            // with no folder: an administrative project is a project, and a reader that quietly dropped those
            // would answer "which projects do we have" with a subset and no sign that it had.
            .. settings.Projects.Select(project => new AssistantProjectRow(
                project.Id,
                project.Name,
                project.Description,
                project.SourceDirectory,
                project.DefaultProfileLabel,
                project.PluginFields,
                project.GitUrl,
                [.. project.SourceDirectories.Select(repository => new AssistantProjectRepositoryRow(repository.Path, repository.Label))])),
        ];
    }

    // AC-1324: the rows a session's own pane shows Allow/Deny for, read from the same flag — an SDK session only;
    // a TTY session's prompt lives in its own TUI, which the host cannot see into (AC-294).
    public async Task<IReadOnlyList<AssistantPendingPermission>> ListPendingPermissionsAsync()
    {
        var rows = await Task.WhenAll(sessions.All.Select(_PermissionsAsync)).ConfigureAwait(false);

        return [.. rows.SelectMany(row => row)];
    }

    private static async Task<IEnumerable<AssistantPendingPermission>> _PermissionsAsync(ISessionHandle session)
    {
        var permissions = await session.ReadPendingPermissionsAsync().ConfigureAwait(false);

        return permissions.Select(permission => new AssistantPendingPermission(
            session.PaneId, permission.ToolUseId, permission.ToolName, permission.InputJson, permission.SinceUtc));
    }

    // The registered sources and the bound/hidden filter ids, read together — the same rule the Projects
    // workspace itself filters on (AC-797), not a second copy of it. The per-source network calls then run in
    // parallel.
    public async Task<IReadOnlyList<AssistantSharedProjectSourceRow>> ListSharedProjectsAsync()
    {
        var sources = sharedProjectSources.Sources;
        if (sources.Count == 0)
        {
            return [];
        }

        var settings = await projectStore.LoadAsync().ConfigureAwait(false);
        var (boundIds, hiddenIds) = SharedProjectSourceLister.VisibilityFilterIds(settings);

        var results = await Task.WhenAll(
            sources.Select(source => SharedProjectSourceLister.ListWithTimeoutAsync(source, CancellationToken.None)))
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

    // An SDK session's transcript is in memory; a TTY session's is a file its CLI wrote. Both are read through the
    // handle, which decides on which thread each needs to happen (AC-609). A plain terminal has no agent behind it
    // to have written anything, so it is refused the same as a pane id that names nothing.
    public async Task<AssistantTranscript?> ReadTranscriptAsync(string paneId, int count)
    {
        if (sessions.Find(paneId) is not { IsTerminal: false } session)
        {
            return null;
        }

        var slice = await session.ReadTranscriptAsync(count).ConfigureAwait(false);

        return new AssistantTranscript(
            session.PaneId,
            session.Title,
            slice.TotalEntries,
            [.. slice.Entries.Select(entry => new AssistantTranscriptEntry(entry.Kind, entry.Text, entry.ToolResult))]);
    }
}
