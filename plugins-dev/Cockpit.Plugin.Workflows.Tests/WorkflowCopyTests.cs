using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Copying a flow into a new one (#69) — what duplicating, starting from a template and importing a file all do. Two
/// flows sharing a step id are one flow with two names, and the wires, which remember the steps they run between,
/// would follow the wrong one.
/// </summary>
public class WorkflowCopyTests
{
    [Fact]
    public void ACopy_HasItsOwnIds_AndItsWiresFollowThem()
    {
        var source = new Workflow { Id = "w", Name = "Ticket → agent" };
        var trigger = new WorkflowNode { Id = "t1", TypeId = "cockpit.manual", Name = "Start" };
        var notify = new WorkflowNode { Id = "t2", TypeId = "cockpit.notify", Name = "Tell me" };
        source.Nodes.AddRange([trigger, notify]);
        source.Connect(trigger.Id, 0, notify.Id);

        var copy = WorkflowCopy.Of(source, "Ticket → agent");

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Empty(copy.Nodes.Select(node => node.Id).Intersect(["t1", "t2"]));

        // The wire still runs between the same two steps — the new ones.
        var wire = copy.Connections.Single();
        Assert.Equal(copy.Nodes[0].Id, wire.FromNodeId);
        Assert.Equal(copy.Nodes[1].Id, wire.ToNodeId);
    }

    [Fact]
    public void ACopy_KeepsWhatEachStepWasSetTo()
    {
        var source = new Workflow { Id = "w", Name = "Flow" };
        var command = new WorkflowNode { Id = "c", TypeId = "cockpit.command", Name = "Cut the branch", IsTraced = true };
        command.Parameters["Command"] = "git switch -c {branch}";
        source.Nodes.Add(command);

        var copied = WorkflowCopy.Of(source, "Flow").Nodes.Single();

        Assert.Equal("git switch -c {branch}", copied.Parameters["Command"]);
        Assert.True(copied.IsTraced);
    }

    // A flow you have not read is not one that should already be running.
    [Fact]
    public void ACopy_IsNeverArmed_HoweverTheOriginalCame()
    {
        var source = new Workflow { Id = "w", Name = "Flow", IsActive = true };

        Assert.False(WorkflowCopy.Of(source, "Flow").IsActive);
    }

    [Fact]
    public void ATemplateFlow_CarriesTheStepsWhereItsAuthorPutThem()
    {
        // A template lays its steps out left to right; the copy keeps that, or the flow opens as a heap.
        var source = new Workflow { Id = "w", Name = "Flow" };
        source.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start", X = 80, Y = 160 });
        source.Nodes.Add(new WorkflowNode { Id = "b", TypeId = "cockpit.notify", Name = "Tell me", X = 360, Y = 160 });

        var copy = WorkflowCopy.Of(source, "Flow");

        Assert.Equal(new[] { (80.0, 160.0), (360.0, 160.0) }, copy.Nodes.Select(node => (node.X, node.Y)));
    }

    // A hand-edited or truncated file can name a step that is not there. Dropping that wire beats carrying it into a
    // flow that would fail to run for reasons nobody could see on the canvas.
    [Fact]
    public void AWireToAStepThatIsNotInTheFlow_IsDropped()
    {
        var source = new Workflow { Id = "w", Name = "Flow" };
        source.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start" });
        source.Connections.Add(new WorkflowConnection { FromNodeId = "a", FromOutput = 0, ToNodeId = "ghost" });

        Assert.Empty(WorkflowCopy.Of(source, "Flow").Connections);
    }
}
