namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// One step in a workflow (#69). What a node <em>does</em> is decided by its <see cref="Kind"/> and its
/// <see cref="TypeId"/> — "text-match", "notify", "delegate" — not by the class, because the whole point is that
/// plugins contribute node types the editor has never heard of. The editor knows only that a node has a place, a
/// name and pins.
/// </summary>
public sealed class WorkflowNode
{
    public required string Id { get; init; }

    /// <summary>What the node type is called in its catalogue ("github.pr-opened", "cockpit.notify") — the key the engine will resolve to an implementation.</summary>
    public required string TypeId { get; init; }

    public required WorkflowNodeKind Kind { get; init; }

    /// <summary>What the operator sees on the node. Defaults to the type's own name; they may rename it, since three "notify" nodes in one flow are otherwise indistinguishable.</summary>
    public required string Title { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>The node type's own settings (a regex to match, a message to send), kept as opaque key/value so the editor never needs to know what a node type stores.</summary>
    public Dictionary<string, string> Settings { get; init; } = [];

    /// <summary>A trigger is what starts a flow, so it takes nothing in; everything else does.</summary>
    public bool HasInput => Kind != WorkflowNodeKind.Trigger;

    /// <summary>
    /// How many ways out this node has. A decision has two — the branch where its condition held and the one
    /// where it did not — which is exactly why the count lives here and not as a constant on the canvas.
    /// </summary>
    public int OutputCount => Kind switch
    {
        WorkflowNodeKind.Decision => 2,
        _ => 1,
    };

    /// <summary>What each way out is called, shown on the pin so a branch is readable without opening the node.</summary>
    public IReadOnlyList<string> OutputLabels => Kind switch
    {
        WorkflowNodeKind.Decision => ["yes", "no"],
        _ => [""],
    };
}
