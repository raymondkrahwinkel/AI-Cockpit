namespace Cockpit.Plugin.Workflows.Model;

// Questions about the shape of a flow (#69) — asked of the wires, not of the order things happened to run in.
//
// The one that matters: which steps can this step actually reach? A step can only refer to the data of a step the
// wires lead back to. Listing every step that merely *ran* earlier is worse than listing none: it offers you
// a field that will never arrive, in a run where nothing warns you that it did not.
public static class WorkflowGraph
{
    // Every step upstream of `nodeId` — the ones whose data can reach it, following the wires backwards, however far.
    public static IReadOnlyList<WorkflowNode> Ancestors(Workflow workflow, string nodeId)
    {
        var found = new List<WorkflowNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { nodeId };
        var pending = new Queue<string>();
        pending.Enqueue(nodeId);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            foreach (var connection in workflow.Connections.Where(connection => connection.ToNodeId == current))
            {
                // A flow may loop, so a step can be its own ancestor by a long enough path. Seen once is enough.
                if (!seen.Add(connection.FromNodeId))
                {
                    continue;
                }

                if (workflow.Node(connection.FromNodeId) is { } node)
                {
                    found.Add(node);
                }

                pending.Enqueue(connection.FromNodeId);
            }
        }

        return found;
    }
}
