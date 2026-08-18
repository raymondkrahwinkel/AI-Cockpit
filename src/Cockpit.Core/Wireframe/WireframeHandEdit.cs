using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe.Model;

namespace Cockpit.Core.Wireframe;

// Which WireframeComponentEdit one operator gesture on the surface is (AC-875), worked out against the parsed tree.
// Here rather than in the panel because it is arithmetic on the format, and an off-by-one moves a component past the
// wrong neighbour.
public static class WireframeHandEdit
{
    // The container a component sits in and its index among that container's children, or null for the screen line —
    // that one has no container to be reordered or given a sibling in.
    public static (WireframeNode Parent, int Index)? Placement(WireframeNode root, string id)
    {
        foreach (var parent in Containers(root))
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
    // than at the end of the container. Null for the screen line, which has no siblings.
    public static WireframeComponentEdit? AddSibling(WireframeNode root, string id, string type, string? text) =>
        Placement(root, id) is { Parent.Id: { } parentId } at
            ? WireframeComponentEdit.Add(parentId, type, text, modifiers: null, position: at.Index + 1)
            : null;

    // One step up or down among its own siblings, or null when there is no such step — the screen line, or a
    // component already at that end.
    //
    // A move's position names the sibling the component ends up in front of, counted in the source as it stands
    // *before* the move. One step up is therefore the neighbour above (`Index - 1`), but one step down is the sibling
    // *past* the neighbour below (`Index + 2`) — naming that neighbour itself would insert in front of it and change
    // nothing.
    public static WireframeComponentEdit? Reorder(WireframeNode root, string id, int delta)
    {
        if (Placement(root, id) is not { Parent.Id: { } parentId } at)
        {
            return null;
        }

        if (delta < 0 ? at.Index == 0 : at.Index == at.Parent.Children.Count - 1)
        {
            return null;
        }

        return WireframeComponentEdit.Move(id, parentId, delta < 0 ? at.Index - 1 : at.Index + 2);
    }

    // Every container this component could be moved into: not itself, not one it already contains — both of which the
    // editor refuses anyway — and not the container it is already the last child of, which would change nothing.
    public static IReadOnlyList<WireframeNode> Destinations(WireframeNode root, string id)
    {
        if (Find(root, id) is not { } node)
        {
            return [];
        }

        var inside = Containers(node).ToHashSet();
        var placement = Placement(root, id);
        return Containers(root)
            .Where(candidate => candidate.Id is not null && !inside.Contains(candidate))
            .Where(candidate => placement is not { } at || candidate != at.Parent || at.Index != at.Parent.Children.Count - 1)
            .ToList();
    }

    // The component named by `id`, or null when no component in this tree carries it.
    public static WireframeNode? Find(WireframeNode node, string id) =>
        node.Id == id ? node : node.Children.Select(child => Find(child, id)).FirstOrDefault(found => found is not null);

    // The component on `line` — the one handle left that is a line number: a click lands on a control that knows which
    // line it was drawn from, which is how a component with no id yet gets named at all (AC-906).
    public static WireframeNode? Find(WireframeNode node, int line) =>
        node.Line == line ? node : node.Children.Select(child => Find(child, line)).FirstOrDefault(found => found is not null);

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
