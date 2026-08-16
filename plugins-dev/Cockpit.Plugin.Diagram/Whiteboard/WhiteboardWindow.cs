using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// A whiteboard as its own window beside the cockpit (AC-842), same shape as DiagramWindow (AC-834):
// `ShowDialogAsync` already opens a non-modal, draggable, resizable window (AC-367).
internal static class WhiteboardWindow
{
    // Keyed on the board, not the session (AC-834 Q4's precedent): the same board opened twice comes forward
    // rather than opening twice — trivially true today since every open creates a fresh board.
    public static string KeyFor(string surfaceId) => $"whiteboard.document.{surfaceId}";

    // `sessionPaneId` is the session already running that this board couples to on open (AC-842: no separate
    // session-picker dialog, the window model fixes the coupling at open time). Null opens with no agent on it.
    public static Task OpenAsync(ICockpitHost host, string? sessionPaneId)
    {
        var surfaceId = Guid.NewGuid().ToString("n");
        return host.ShowDialogAsync(
            "Whiteboard",
            () => new WhiteboardWorkspaceBody(host, surfaceId, sessionPaneId),
            KeyFor(surfaceId),
            width: 900,
            height: 640);
    }
}
