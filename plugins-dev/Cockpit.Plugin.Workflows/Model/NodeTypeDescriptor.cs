namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// What a node type <em>is</em> (#69): the thing the picker lists, the canvas draws and the engine will later
/// execute. A node on the canvas is an instance of one of these, with its own name and parameter values — the
/// same separation n8n makes, and the reason a plugin can one day contribute a type the editor has never heard of.
/// </summary>
/// <param name="Id">The key the engine resolves to an implementation ("cockpit.notify").</param>
/// <param name="Name">What the picker and the node show.</param>
/// <param name="Description">One line, shown in the picker — what this does, in the operator's words.</param>
/// <param name="Icon">A single glyph. The cockpit has no icon set, and a letter in a box reads worse than a symbol everyone already knows.</param>
/// <param name="Category">Where the picker files it.</param>
/// <param name="Kind">Trigger, action or decision — all the canvas and the engine need to know structurally.</param>
/// <param name="Outputs">What the ways out are called. One unnamed way out for most; "true"/"false" for a decision.</param>
/// <param name="Parameters">What this type can be configured with. Names only for now — the editor for them comes with the node detail view.</param>
public sealed record NodeTypeDescriptor(
    string Id,
    string Name,
    string Description,
    string Icon,
    NodeCategory Category,
    WorkflowNodeKind Kind,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Parameters,
    IReadOnlyDictionary<string, string>? Sample = null,
    string? Group = null,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>>? Suggest = null)
{
    /// <summary>
    /// The heading the picker files this under. A cockpit type belongs to one of the fixed categories; a step a
    /// plugin contributed belongs under that plugin's own name ("YOUTRACK"), because <see cref="NodeCategory"/> is a
    /// list of the things this app knows about, and a plugin is by definition not on it.
    /// </summary>
    public string Heading => Group ?? Category switch
    {
        NodeCategory.Trigger => "STARTS A FLOW",
        NodeCategory.Sessions => "SESSIONS",
        NodeCategory.Notify => "TELL ME",
        NodeCategory.External => "OUTSIDE THE COCKPIT",
        NodeCategory.Flow => "FLOW",
        _ => "OTHER",
    };

    /// <summary>
    /// What this kind of step typically hands on, with an example value — so a step you have not run yet can still
    /// say what will come out of it. Not a guess dressed as fact: the dialog labels it an example, and the moment a
    /// run has happened the real data replaces it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Produces => Sample ?? _nothing;

    private static readonly Dictionary<string, string> _nothing = [];

    /// <summary>A trigger is where a run begins, so nothing flows into it.</summary>
    public bool HasInput => Kind != WorkflowNodeKind.Trigger;
}
