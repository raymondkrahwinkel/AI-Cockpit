namespace Cockpit.Plugin.Diagram.Collab;

// AC-910: the wording of an "Ask the agent…" reference — one shared format for diagram, whiteboard and wireframe
// (replaces AC-849's PinMessage, which named only the surface's title), naming surface kind/id/name plus the object
// so it never reads ambiguous with several windows open. Document-derived text is folded to one line; the question isn't.
internal static class AskMessage
{
    public static string Compose(AskContext context, string question) =>
        $"🗨️ Ask the agent · {context.SurfaceKind} \"{SingleLineText.Fold(context.SurfaceName)}\" (id {SingleLineText.Fold(context.SurfaceId)}){_ObjectPart(context)} — {question}";

    private static string _ObjectPart(AskContext context)
    {
        var pieces = new[] { context.ObjectRef, context.ObjectLabel }
            .Where(piece => !string.IsNullOrEmpty(piece))
            .Select(piece => SingleLineText.Fold(piece!))
            .ToList();

        return pieces.Count == 0 ? "" : $" · {string.Join(" — ", pieces)}";
    }
}
