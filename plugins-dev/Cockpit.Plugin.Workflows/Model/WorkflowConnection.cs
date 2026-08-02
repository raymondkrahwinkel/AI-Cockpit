namespace Cockpit.Plugin.Workflows.Model;

// A wire: the run leaves `FromNodeId` through its `FromOutput`-th way out and arrives at
// `ToNodeId`. The output index is what makes a decision's two branches distinguishable — without it
// "yes" and "no" would be the same edge.
public sealed class WorkflowConnection
{
    public required string FromNodeId { get; init; }

    public required int FromOutput { get; init; }

    public required string ToNodeId { get; init; }
}
