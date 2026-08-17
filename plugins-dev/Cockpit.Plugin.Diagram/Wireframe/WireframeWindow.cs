using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Wireframe;

// A wireframe as its own window beside the cockpit (AC-873), same shape as DiagramWindow (AC-834) and
// WhiteboardWindow (AC-842): `ShowDialogAsync` already opens a non-modal, draggable, resizable window (AC-367).
internal static class WireframeWindow
{
    // Keyed on the document, not the session (AC-834 Q4's precedent): one session can touch several wireframes, and
    // the same wireframe opened twice has to bring the existing window forward rather than open a second one.
    public static string KeyFor(string documentId) => $"wireframe.document.{documentId}";

    // `sessionPaneId` is the session already running that this wireframe couples to on open — named by whoever
    // opens the window, never guessed here. Null opens with no agent on it, which is a state the surface draws.
    public static Task OpenAsync(ICockpitHost host, WireframeDocument document, string? sessionPaneId) =>
        host.ShowDialogAsync(
            document.Title,
            () => new WireframeWorkspaceBody(host, document, sessionPaneId),
            KeyFor(document.Id),
            width: 960,
            height: 680);
}
