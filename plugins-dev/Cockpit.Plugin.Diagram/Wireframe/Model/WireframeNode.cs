namespace Cockpit.Plugin.Diagram.Wireframe.Model;

// One component, one source line. A class rather than a record on purpose: the tree is mutable (WF-5 edits it in
// place) and identity is what the rendered control points back at, so value equality would be a lie.
internal sealed class WireframeNode(WireframeNodeKind kind, int line, string? text = null)
{
    public WireframeNodeKind Kind { get; } = kind;

    public int Line { get; } = line;

    public string? Text { get; set; } = text;

    public List<WireframeModifier> Modifiers { get; } = [];

    public List<WireframeNode> Children { get; } = [];

    // Which kinds may have indented lines under them. A widget with children is nearly always a mis-indent, so the
    // parser says so instead of dropping the lines.
    public bool IsContainer => Kind is WireframeNodeKind.Screen
        or WireframeNodeKind.Row
        or WireframeNodeKind.Column
        or WireframeNodeKind.Group
        or WireframeNodeKind.Tabs
        or WireframeNodeKind.Tab
        or WireframeNodeKind.Nav
        or WireframeNodeKind.List
        or WireframeNodeKind.Table;

    public bool Has(WireframeModifierName name) => Modifiers.Any(modifier => modifier.Name == name);

    public string? ValueOf(WireframeModifierName name) =>
        Modifiers.FirstOrDefault(modifier => modifier.Name == name)?.Value;

    // The flex weight of w:/h:, or null when the component did not ask for one — the layout then sizes it to its
    // content instead of to a share of the space.
    public int? WeightOf(WireframeModifierName name) =>
        int.TryParse(ValueOf(name), out var weight) ? weight : null;

    public WireframeAlignment? Alignment =>
        Enum.TryParse<WireframeAlignment>(ValueOf(WireframeModifierName.Align), ignoreCase: true, out var alignment)
            ? alignment
            : null;
}
