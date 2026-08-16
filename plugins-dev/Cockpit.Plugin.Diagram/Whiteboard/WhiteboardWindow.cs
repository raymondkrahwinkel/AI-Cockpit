using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// A whiteboard as its own window beside the cockpit (AC-842), same shape as DiagramWindow (AC-834):
// `ShowDialogAsync` already opens a non-modal, draggable, resizable window (AC-367).
internal static class WhiteboardWindow
{
    // Keyed on the document, not the session (AC-834 Q4's precedent, W-2/AC-843): a saved board's id is its file
    // path, so reopening it from the list brings the existing window forward rather than opening a second one.
    public static string KeyFor(string documentId) => $"whiteboard.document.{documentId}";

    // `sessionPaneId` is the session already running that this board couples to on open (AC-842: no separate
    // session-picker dialog, the window model fixes the coupling at open time). Null opens with no agent on it.
    public static Task OpenAsync(ICockpitHost host, WhiteboardDocument document, string? sessionPaneId) =>
        host.ShowDialogAsync(
            document.Title,
            () => new WhiteboardWorkspaceBody(host, document, sessionPaneId),
            KeyFor(document.Id),
            width: 900,
            height: 640);
}
