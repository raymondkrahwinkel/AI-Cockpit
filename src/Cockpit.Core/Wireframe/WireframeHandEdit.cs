using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// Which WireframeComponentEdit one operator gesture on the surface is (AC-875), worked out against the parsed tree.
// Here rather than in the panel because it is arithmetic on the format, and an off-by-one moves a component past the
// wrong neighbour.
public static class WireframeHandEdit
{
    // The container a component sits in and its index among that container's children, or null for a screen line —
    // those have no container to be reordered or given a sibling in.
    public static (WireframeNode Parent, int Index)? Placement(IReadOnlyList<WireframeNode> screens, string id)
    {
        foreach (var parent in screens.SelectMany(Containers))
        {
            var index = parent.Children.FindIndex(child => child.Id == id);
            if (index >= 0)
            {
                return (parent, index);
            }
        }

        return null;
    }

    // Adds a component inside the container named by `parentId`, after what is already in it.
    public static WireframeComponentEdit AddChild(string parentId, string type, string? text) =>
        WireframeComponentEdit.Add(parentId, type, text, modifiers: null, position: null);

    // Adds a component straight after the one named by `id`, in the same container — where the eye already is, rather
    // than at the end of the container. Null for a screen line, which has no siblings.
    public static WireframeComponentEdit? AddSibling(IReadOnlyList<WireframeNode> screens, string id, string type, string? text) =>
        Placement(screens, id) is { Parent.Id: { } parentId } at
            ? WireframeComponentEdit.Add(parentId, type, text, modifiers: null, position: at.Index + 1)
            : null;

    // One step up or down among its own siblings, or null when there is no such step — a screen line, or a component
    // already at that end. A position counts the source as it stands *before* the move, so up is the neighbour above
    // (`Index - 1`) and down the sibling *past* the one below (`Index + 2`); naming that one would change nothing.
    public static WireframeComponentEdit? Reorder(IReadOnlyList<WireframeNode> screens, string id, int delta)
    {
        if (Placement(screens, id) is not { Parent.Id: { } parentId } at)
        {
            return null;
        }

        if (delta < 0 ? at.Index == 0 : at.Index == at.Parent.Children.Count - 1)
        {
            return null;
        }

        return WireframeComponentEdit.Move(id, parentId, delta < 0 ? at.Index - 1 : at.Index + 2);
    }

    // Every container this component could be moved into, across every screen (AC-901): not itself, not one it
    // already contains — both of which the editor refuses anyway — and not the container it is already the last
    // child of, which would change nothing.
    public static IReadOnlyList<WireframeNode> Destinations(IReadOnlyList<WireframeNode> screens, string id)
    {
        if (Find(screens, id) is not { } node)
        {
            return [];
        }

        var inside = Containers(node).ToHashSet();
        var placement = Placement(screens, id);
        return screens.SelectMany(Containers)
            .Where(candidate => candidate.Id is not null && !inside.Contains(candidate))
            .Where(candidate => placement is not { } at || candidate != at.Parent || at.Index != at.Parent.Children.Count - 1)
            .ToList();
    }

    // The container a component sits in, or null for a screen line — Placement's twin for a component the surface
    // has under the pointer (AC-904) rather than one already named by its id.
    public static WireframeNode? ParentOf(IReadOnlyList<WireframeNode> screens, WireframeNode node) =>
        screens.SelectMany(Containers).FirstOrDefault(parent => parent.Children.Contains(node));

    // Whether `into` could take the component named by `id` as a child (AC-904). Unlike Destinations this says
    // nothing about where inside, so the container it already sits in counts: a drag lands it at another index there.
    public static bool CanMoveInto(IReadOnlyList<WireframeNode> screens, string id, WireframeNode into) =>
        into.IsContainer
        && into.Id is not null
        && Find(screens, id) is { } node
        && !screens.Contains(node)
        && Find(node, into.Line) is null;

    // The component named by `id`, or null when no component in this tree carries it.
    public static WireframeNode? Find(WireframeNode node, string id) =>
        node.Id == id ? node : node.Children.Select(child => Find(child, id)).FirstOrDefault(found => found is not null);

    // The same across a whole document.
    public static WireframeNode? Find(IReadOnlyList<WireframeNode> screens, string id) =>
        screens.Select(screen => Find(screen, id)).FirstOrDefault(found => found is not null);

    // The component on `line` — the one handle left that is a line number: a click lands on a control that knows which
    // line it was drawn from, which is how a component with no id yet gets named at all (AC-906).
    public static WireframeNode? Find(WireframeNode node, int line) =>
        node.Line == line ? node : node.Children.Select(child => Find(child, line)).FirstOrDefault(found => found is not null);

    public static WireframeNode? Find(IReadOnlyList<WireframeNode> screens, int line) =>
        screens.Select(screen => Find(screen, line)).FirstOrDefault(found => found is not null);

    // Which screen a component belongs to (AC-901) — what the surface zooms into on a double click, and what a move
    // into another screen's container is named after so it is never a silent jump.
    public static WireframeNode? ScreenOf(IReadOnlyList<WireframeNode> screens, WireframeNode node) =>
        screens.FirstOrDefault(screen => Find(screen, node.Line) is not null);

    // The keyword a component is written as — its enum name in lower case (AC-871's one vocabulary).
    public static string Keyword(WireframeNodeKind kind) => kind.ToString().ToLowerInvariant();

    private static IEnumerable<WireframeNode> Containers(WireframeNode node)
    {
        if (node.IsContainer)
        {
            yield return node;
        }

        foreach (var container in node.Children.SelectMany(Containers))
        {
            yield return container;
        }
    }
}
