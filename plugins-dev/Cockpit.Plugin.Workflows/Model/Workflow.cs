namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// One workflow: its nodes and the wires between them (#69). The rules live here rather than in the canvas, so an
/// engine, an importer and a plugin-contributed node all obey the same ones — and so they can be tested without a
/// mouse.
/// <para>
/// The rules are deliberately few. An earlier version of this file refused fan-out (one way out feeding several
/// steps) and loops, on the assumption that they were mistakes. They are not: in n8n both are ordinary, and a loop
/// with a decision as its stop condition is a normal thing to draw. Refusing them made the editor wrong about
/// workflows rather than strict about them.
/// </para>
/// </summary>
public sealed class Workflow
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public List<WorkflowNode> Nodes { get; init; } = [];

    public List<WorkflowConnection> Connections { get; init; } = [];

    public WorkflowNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);

    /// <summary>
    /// Whether this wire may exist. Only three things are refused, and each is a wire the engine could never
    /// follow: a node feeding itself, anything running <em>into</em> a trigger, and a wire that is already there.
    /// Everything else — fan-out, merging several steps into one, a loop back to an earlier node — is a shape
    /// workflows genuinely have.
    /// </summary>
    public WorkflowConnectionRule CanConnect(string fromNodeId, int fromOutput, string toNodeId)
    {
        if (fromNodeId == toNodeId)
        {
            return WorkflowConnectionRule.Refuse("A step cannot feed itself.");
        }

        if (Node(fromNodeId) is not { } from || Node(toNodeId) is not { } to)
        {
            return WorkflowConnectionRule.Refuse("That step is not in this workflow.");
        }

        if (fromOutput < 0 || fromOutput >= from.Outputs.Count)
        {
            return WorkflowConnectionRule.Refuse($"'{from.Name}' has no such way out.");
        }

        if (!to.HasInput)
        {
            return WorkflowConnectionRule.Refuse($"'{to.Name}' is a trigger: it starts a flow, so nothing runs into it.");
        }

        if (Connections.Any(connection =>
                connection.FromNodeId == fromNodeId
                && connection.FromOutput == fromOutput
                && connection.ToNodeId == toNodeId))
        {
            return WorkflowConnectionRule.Refuse("That connection is already there.");
        }

        return WorkflowConnectionRule.Allow();
    }

    /// <summary>Adds the wire when the rules allow it, and says why when they do not.</summary>
    public WorkflowConnectionRule Connect(string fromNodeId, int fromOutput, string toNodeId)
    {
        var rule = CanConnect(fromNodeId, fromOutput, toNodeId);
        if (rule.IsAllowed)
        {
            Connections.Add(new WorkflowConnection { FromNodeId = fromNodeId, FromOutput = fromOutput, ToNodeId = toNodeId });
        }

        return rule;
    }

    /// <summary>Removes a node and every wire that touched it — a wire to a step that is gone is not a wire.</summary>
    public void Remove(string nodeId)
    {
        Nodes.RemoveAll(node => node.Id == nodeId);
        Connections.RemoveAll(connection => connection.FromNodeId == nodeId || connection.ToNodeId == nodeId);
    }

    public void Disconnect(WorkflowConnection connection) => Connections.Remove(connection);

    /// <summary>Whether this way out already leads somewhere — what decides if the canvas draws the "+" that adds the next step.</summary>
    public bool HasConnectionFrom(string nodeId, int output) =>
        Connections.Any(connection => connection.FromNodeId == nodeId && connection.FromOutput == output);
}
