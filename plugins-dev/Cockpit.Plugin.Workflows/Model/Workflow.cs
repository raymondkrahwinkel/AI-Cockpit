namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// One workflow: its nodes and the wires between them (#69). The rules about what may be wired to what live here
/// rather than in the canvas, so an engine, an importer or a plugin-contributed node all obey the same ones — and
/// so they can be tested without a mouse.
/// </summary>
public sealed class Workflow
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    public List<WorkflowNode> Nodes { get; init; } = [];

    public List<WorkflowConnection> Connections { get; init; } = [];

    public WorkflowNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);

    /// <summary>
    /// Whether this wire may exist. Refusing is the point: a connection the engine could never follow is worse
    /// than no connection, because the canvas would show a flow that does not do what it looks like it does.
    /// </summary>
    public WorkflowConnectionRule CanConnect(string fromNodeId, int fromOutput, string toNodeId)
    {
        if (fromNodeId == toNodeId)
        {
            return WorkflowConnectionRule.Refuse("A node cannot feed itself.");
        }

        if (Node(fromNodeId) is not { } from || Node(toNodeId) is not { } to)
        {
            return WorkflowConnectionRule.Refuse("That node is not in this workflow.");
        }

        if (fromOutput < 0 || fromOutput >= from.OutputCount)
        {
            return WorkflowConnectionRule.Refuse($"'{from.Title}' has no such way out.");
        }

        if (!to.HasInput)
        {
            return WorkflowConnectionRule.Refuse($"'{to.Title}' is a trigger: it starts a flow, so nothing runs into it.");
        }

        // One wire per way out. A step that ran two things at once would need the engine to say which came first,
        // and a flow whose order you cannot read is not a flow you can trust.
        if (Connections.Any(connection => connection.FromNodeId == fromNodeId && connection.FromOutput == fromOutput))
        {
            return WorkflowConnectionRule.Refuse($"'{from.Title}' already continues from there.");
        }

        if (_WouldCycle(fromNodeId, toNodeId))
        {
            return WorkflowConnectionRule.Refuse("That would make the flow loop back on itself.");
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

    /// <summary>Removes a node and every wire that touched it — a wire to a node that is gone is not a wire.</summary>
    public void Remove(string nodeId)
    {
        Nodes.RemoveAll(node => node.Id == nodeId);
        Connections.RemoveAll(connection => connection.FromNodeId == nodeId || connection.ToNodeId == nodeId);
    }

    // Walking forward from the would-be target: if we can reach the source again, the wire closes a loop.
    private bool _WouldCycle(string fromNodeId, string toNodeId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(toNodeId);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current == fromNodeId)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var next in Connections.Where(connection => connection.FromNodeId == current))
            {
                pending.Push(next.ToNodeId);
            }
        }

        return false;
    }
}
