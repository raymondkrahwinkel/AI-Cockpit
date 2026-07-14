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

    /// <summary>
    /// Whether this flow is armed (#69). A flow you are still drawing should not fire the moment its trigger
    /// happens, and a flow you want to pause is not a flow you want to delete — so switching it off is a first-class
    /// thing, exactly as it is in n8n's Active/Inactive.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>When it was last changed — what the manager sorts and shows, so "which one was I working on" has an answer.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

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

    /// <summary>
    /// Keeps a node's wires on the ways out they were drawn to, after those ways out changed — which only a switch's
    /// do (its pins are the cases written into it, see <see cref="SwitchCases"/>). A wire remembers its way out by
    /// <em>position</em>, so inserting a case in front of the others would silently hand every wire below it to the
    /// wrong branch, and the error wire — which sits one past the last ordinary pin — would become an ordinary one.
    /// A wire drawn from "Review" belongs to "Review", so they are matched up again by name.
    /// <para>
    /// A wire whose case no longer exists is removed and its label returned: the operator deleted that case, and a
    /// wire from a pin that is gone is not a wire. Saying which ones went is the point — silently dropping them is how
    /// a flow quietly stops doing something it used to do.
    /// </para>
    /// </summary>
    /// <param name="nodeId">The node whose ways out changed.</param>
    /// <param name="before">What its ways out were called before the change, in their old order.</param>
    /// <returns>The labels of the wires that had nowhere left to go, in the order they were dropped.</returns>
    public IReadOnlyList<string> RewireOutputs(string nodeId, IReadOnlyList<string> before)
    {
        if (Node(nodeId) is not { } node)
        {
            return [];
        }

        var after = node.Outputs;
        var dropped = new List<string>();

        foreach (var connection in Connections.Where(connection => connection.FromNodeId == nodeId).ToList())
        {
            // The error pin is not in Outputs — it sits one past them — so it is named here rather than looked up.
            var label = connection.FromOutput == before.Count
                ? "error"
                : before.ElementAtOrDefault(connection.FromOutput);

            var moved = label switch
            {
                null => -1,
                "error" => after.Count,
                _ => _IndexOf(after, label),
            };

            if (moved < 0)
            {
                Connections.Remove(connection);
                dropped.Add(label ?? "a way out that no longer exists");
                continue;
            }

            if (moved != connection.FromOutput)
            {
                Connections[Connections.IndexOf(connection)] = new WorkflowConnection
                {
                    FromNodeId = connection.FromNodeId,
                    FromOutput = moved,
                    ToNodeId = connection.ToNodeId,
                };
            }
        }

        return dropped;
    }

    private static int _IndexOf(IReadOnlyList<string> labels, string label)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            if (string.Equals(labels[index], label, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Whether this way out already leads somewhere — what decides if the canvas draws the "+" that adds the next step.</summary>
    public bool HasConnectionFrom(string nodeId, int output) =>
        Connections.Any(connection => connection.FromNodeId == nodeId && connection.FromOutput == output);
}
