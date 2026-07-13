namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// A wire: the run leaves <see cref="FromNodeId"/> through its <see cref="FromOutput"/>-th way out and arrives at
/// <see cref="ToNodeId"/>. The output index is what makes a decision's two branches distinguishable — without it
/// "yes" and "no" would be the same edge.
/// </summary>
public sealed class WorkflowConnection
{
    public required string FromNodeId { get; init; }

    public required int FromOutput { get; init; }

    public required string ToNodeId { get; init; }
}
