using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;

namespace Cockpit.App.Services;

/// <summary>
/// The app-level half of <see cref="IAssistantReadGateway"/> (AC-544): the running session panels, read across
/// every workspace at once.
/// </summary>
/// <remarks>
/// Deliberately close in shape to <see cref="WorkspaceAgentGateway"/> and deliberately not the same class. That one
/// answers "who shares this caller's desk" and derives the desk from the caller; this one answers "what is running
/// anywhere" and has no caller to derive anything from — the assistant sits on no desk. Folding the second question
/// into the first would have meant giving that gateway a mode in which it does not scope, and a scoping rule with
/// an off switch is the thing AC-544 exists to avoid handing out.
/// <para>
/// The UI-thread marshalling is the same and for the same reason: <c>CockpitViewModel.Sessions</c> only ever mutates
/// on the UI thread and an MCP tool call arrives on a Kestrel request thread. Inline when already there, so a unit
/// test on the UI thread pays for no redundant dispatch, and the awaitable is handed back rather than blocked on.
/// </para>
/// <para>
/// <b>Every session, including the ones the grid does not show.</b> <see cref="CockpitViewModel.AllSessions"/> holds
/// embedded panes (an Autopilot step, a plugin run) as well as the ones in the layout — they are full agent sessions
/// with their own MCP tokens, and a status question that skipped them would be answered confidently and wrongly.
/// Plain terminal panes are left out: they carry a pane id but there is no agent on the other end to have a status.
/// The assistant's own session is left out too — it is the one asking.
/// </para>
/// </remarks>
internal sealed class AssistantReadGateway(CockpitViewModel cockpit) : IAssistantReadGateway, ISingletonService
{
    public Task<IReadOnlyList<AssistantSessionRow>> ListSessionsAsync() =>
        Dispatcher.UIThread.CheckAccess()
            ? Task.FromResult(_ListSessions())
            : Dispatcher.UIThread.InvokeAsync(_ListSessions).GetTask();

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
                        workspaceId is not null && namesById.TryGetValue(workspaceId, out var name) ? name : null);
                }),
        ];
    }
}
