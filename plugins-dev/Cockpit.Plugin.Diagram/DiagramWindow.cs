using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// A diagram as its own window beside the cockpit (AC-834). Nothing is built here: `ShowDialogAsync` already opens a
// non-modal, draggable, resizable window (AC-367) and folds a repeat ask into the open one (`SurfaceWindows`).
internal static class DiagramWindow
{
    // Keyed on the document, not on the session (AC-834 Q4): one session touches several diagrams, and the same
    // diagram opened twice has to come forward rather than open twice.
    public static string KeyFor(string documentId) => $"diagram.document.{documentId}";

    // `sessionPaneId` is the session the operator is already talking to — named by whoever opens the window, never
    // guessed here. Null opens the diagram with no agent on it, which is a state the surface draws.
    public static Task OpenAsync(ICockpitHost host, DiagramDocument document, string? sessionPaneId) =>
        host.ShowDialogAsync(
            document.Title,
            () => new DiagramWorkspaceBody(host, document, sessionPaneId),
            KeyFor(document.Id),
            width: 900,
            height: 640);
}
