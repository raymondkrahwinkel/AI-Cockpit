using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Which steps a step can reach (#69). The dialog offers you their fields, so getting this wrong is not cosmetic: a
/// field from a step on another branch will never arrive, and the placeholder that names it resolves to nothing in a
/// run where nothing warns you. Offering it is worse than offering nothing.
/// </summary>
public class WorkflowGraphTests
{
    [Fact]
    public void OnlyTheStepsTheWiresLeadBackTo_AreReachable()
    {
        //   trigger → a → c
        //   b (on its own)
        var workflow = _Flow();
        _Wire(workflow, "trigger", "a");
        _Wire(workflow, "a", "c");

        WorkflowGraph.Ancestors(workflow, "c").Select(node => node.Id)
            .Should().BeEquivalentTo(["trigger", "a"], "b is not upstream of c, however early it ran");
    }

    [Fact]
    public void AStepOnAParallelBranch_IsNotReachable()
    {
        //   trigger → a
        //   trigger → b
        var workflow = _Flow();
        _Wire(workflow, "trigger", "a");
        _Wire(workflow, "trigger", "b");

        WorkflowGraph.Ancestors(workflow, "a").Select(node => node.Id).Should().BeEquivalentTo(["trigger"]);
    }

    [Fact]
    public void ReachabilityGoesAllTheWayBack_NotJustOneStep()
    {
        var workflow = _Flow();
        _Wire(workflow, "trigger", "a");
        _Wire(workflow, "a", "b");
        _Wire(workflow, "b", "c");

        WorkflowGraph.Ancestors(workflow, "c").Select(node => node.Id)
            .Should().BeEquivalentTo(["trigger", "a", "b"]);
    }

    [Fact]
    public void ALoop_DoesNotHangTheWalk()
    {
        // A flow may loop, and a step can be its own ancestor by a long enough path.
        var workflow = _Flow();
        _Wire(workflow, "trigger", "a");
        _Wire(workflow, "a", "b");
        _Wire(workflow, "b", "a");

        WorkflowGraph.Ancestors(workflow, "b").Select(node => node.Id).Should().BeEquivalentTo(["trigger", "a"]);
    }

    [Fact]
    public void ATriggerReachesNothing_BecauseItIsWhereARunBegins()
    {
        var workflow = _Flow();
        _Wire(workflow, "trigger", "a");

        WorkflowGraph.Ancestors(workflow, "trigger").Should().BeEmpty();
    }

    private static Workflow _Flow()
    {
        var workflow = new Workflow { Id = "w", Name = "Flow" };

        foreach (var id in new[] { "trigger", "a", "b", "c" })
        {
            workflow.Nodes.Add(new WorkflowNode { Id = id, TypeId = "cockpit.notify", Name = id });
        }

        return workflow;
    }

    private static void _Wire(Workflow workflow, string from, string to) =>
        workflow.Connections.Add(new WorkflowConnection { FromNodeId = from, FromOutput = 0, ToNodeId = to });
}
