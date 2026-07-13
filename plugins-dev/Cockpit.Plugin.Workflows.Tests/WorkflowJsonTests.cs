using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// A flow has to survive being written and read back (#69) — to disk, to a file you can put in git, to a paste
/// into someone else's cockpit. A workflow that cannot round-trip is a drawing.
/// </summary>
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
                new WorkflowNode { Id = "t", TypeId = "github.pr-opened", Kind = WorkflowNodeKind.Trigger, Title = "PR opened", X = 60, Y = 40 },
                new WorkflowNode
                {
                    Id = "a",
                    TypeId = "cockpit.notify",
                    Kind = WorkflowNodeKind.Action,
                    Title = "Notify me",
                    X = 380,
                    Y = 40,
                    Settings = { ["message"] = "Review requested" },
                },
            },
            Connections = { new WorkflowConnection { FromNodeId = "t", FromOutput = 0, ToNodeId = "a" } },
        };

        var loaded = WorkflowJson.Read(WorkflowJson.Write(workflow));

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("PR review");
        loaded.Nodes.Should().HaveCount(2);
        loaded.Node("t")!.Kind.Should().Be(WorkflowNodeKind.Trigger);
        loaded.Node("a")!.X.Should().Be(380);
        loaded.Node("a")!.Settings["message"].Should().Be("Review requested");
        loaded.Connections.Should().ContainSingle(connection => connection.FromNodeId == "t" && connection.ToNodeId == "a");
    }

    [Fact]
    public void Write_UsesTheNamesOfTheKinds_SoAHandEditedFlowStaysReadable()
    {
        var workflow = new Workflow
        {
            Id = "w",
            Name = "Flow",
            Nodes = { new WorkflowNode { Id = "t", TypeId = "cockpit.event", Kind = WorkflowNodeKind.Trigger, Title = "Trigger" } },
        };

        WorkflowJson.Write(workflow).Should().Contain("\"Trigger\"").And.NotContain("\"Kind\": 0");
    }

    [Fact]
    public void Read_OfSomethingThatIsNotAWorkflow_CostsYouThatFlowRatherThanThePlugin()
    {
        WorkflowJson.Read("{ this is not json").Should().BeNull();
        WorkflowJson.ReadAll("nonsense").Should().BeEmpty();
        WorkflowJson.ReadAll(null).Should().BeEmpty();
    }

    [Fact]
    public void ReadAll_ReadsBackEveryFlowThatWasWritten()
    {
        var flows = new List<Workflow>
        {
            new() { Id = "1", Name = "First" },
            new() { Id = "2", Name = "Second" },
        };

        WorkflowJson.ReadAll(WorkflowJson.WriteAll(flows)).Select(flow => flow.Name).Should().Equal("First", "Second");
    }
}
