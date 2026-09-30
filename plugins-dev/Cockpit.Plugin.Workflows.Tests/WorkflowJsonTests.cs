using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// A flow has to survive being written and read back (#69) — to disk, to a file you can put in git, to a paste
// into someone else's cockpit. A workflow that cannot round-trip is a drawing.
public class WorkflowJsonTests
{
    [Fact]
    public void RoundTrip_KeepsTheNodesTheirPlacesAndTheWiresBetweenThem()
    {
        var workflow = new Workflow
        {
            Id = "w",
            Name = "PR review",
            Nodes =
            {
                new WorkflowNode { Id = "t", TypeId = "cockpit.text-match", Name = "PR opened", X = 60, Y = 40 },
                new WorkflowNode
                {
                    Id = "a",
                    TypeId = "cockpit.notify",
                    Name = "Notify me",
                    X = 380,
                    Y = 40,
                    Parameters = { ["Message"] = "Review requested" },
                },
            },
            Connections = { new WorkflowConnection { FromNodeId = "t", FromOutput = 0, ToNodeId = "a" } },
        };

        var loaded = WorkflowJson.Read(WorkflowJson.Write(workflow));

        Assert.NotNull(loaded);
        Assert.Equal("PR review", loaded!.Name);
        Assert.Equal(2, System.Linq.Enumerable.Count(loaded.Nodes));
        Assert.Equal(WorkflowNodeKind.Trigger, loaded.Node("t")!.Kind);
        Assert.Equal(380, loaded.Node("a")!.X);
        Assert.Equal("Review requested", loaded.Node("a")!.Parameters["Message"]);
        Assert.Single(loaded.Connections, connection => connection.FromNodeId == "t" && connection.ToNodeId == "a");
    }

    [Fact]
    public void Read_OfSomethingThatIsNotAWorkflow_CostsYouThatFlowRatherThanThePlugin()
    {
        Assert.Null(WorkflowJson.Read("{ this is not json"));
        Assert.Empty(WorkflowJson.ReadAll("nonsense"));
        Assert.Empty(WorkflowJson.ReadAll(null));
    }
}
