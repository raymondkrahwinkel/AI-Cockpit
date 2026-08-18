namespace Cockpit.Plugin.Diagram.Wireframe;

// The wireframe a window stands for (AC-873), same shape as DiagramDocument (AC-834): `Id` is what the window keys
// on, one window per document rather than per session, so it has to be the wireframe's own stable identity — a
// fresh id for one that has no file yet (WF-4 gives it one on first save).
internal sealed record WireframeDocument(string Id, string Title, string Text, string? FilePath = null)
{
    // A single, childless screen (AC-871's minimal valid source): renders as a blank sketch, not an error.
    public const string Empty = "screen \"New screen\"";

    public static WireframeDocument New(string title) => new(Guid.NewGuid().ToString("n"), title, Empty);
}
