namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// A flow copied into a new one, ids and all (#69). Three things need exactly this — duplicating a flow, starting one
/// from a template, and importing one somebody sent you — and they need it for the same reason: two flows sharing a
/// step id are one flow with two names, and the wires, which remember the steps they run between, would follow the
/// wrong one.
/// </summary>
public static class WorkflowCopy
{
    /// <summary>A fresh flow with the same steps and wires, under <paramref name="name"/>. Never armed: a flow you have not read yet is not one that should already be running.</summary>
    public static Workflow Of(Workflow source, string name)
    {
        var copy = new Workflow
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            IsActive = false,
        };

        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in source.Nodes)
        {
            var id = Guid.NewGuid().ToString("n");
            idMap[node.Id] = id;
            copy.Nodes.Add(new WorkflowNode
            {
                Id = id,
                TypeId = node.TypeId,
                Name = node.Name,
                X = node.X,
                Y = node.Y,
                IsDisabled = node.IsDisabled,
                IsTraced = node.IsTraced,
                ContinueOnError = node.ContinueOnError,
                Parameters = new Dictionary<string, string>(node.Parameters),
            });
        }

        foreach (var connection in source.Connections)
        {
            // A wire to a step that is not in the flow is not a wire — a hand-edited or truncated file is dropped
            // here rather than carried into a flow that would fail to run for reasons nobody could see.
            if (!idMap.TryGetValue(connection.FromNodeId, out var from) || !idMap.TryGetValue(connection.ToNodeId, out var to))
            {
                continue;
            }

            copy.Connections.Add(new WorkflowConnection
            {
                FromNodeId = from,
                FromOutput = connection.FromOutput,
                ToNodeId = to,
            });
        }

        return copy;
    }
}
