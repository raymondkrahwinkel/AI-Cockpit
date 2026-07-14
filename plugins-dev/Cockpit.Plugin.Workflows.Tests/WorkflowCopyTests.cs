using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

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

        copy.Id.Should().NotBe(source.Id);
        copy.Nodes.Select(node => node.Id).Should().NotIntersectWith(["t1", "t2"]);

        // The wire still runs between the same two steps — the new ones.
        var wire = copy.Connections.Single();
        wire.FromNodeId.Should().Be(copy.Nodes[0].Id);
        wire.ToNodeId.Should().Be(copy.Nodes[1].Id);
    }

    [Fact]
    public void ACopy_KeepsWhatEachStepWasSetTo()
    {
        var source = new Workflow { Id = "w", Name = "Flow" };
        var command = new WorkflowNode { Id = "c", TypeId = "cockpit.command", Name = "Cut the branch", IsTraced = true };
        command.Parameters["Command"] = "git switch -c {branch}";
        source.Nodes.Add(command);

        var copied = WorkflowCopy.Of(source, "Flow").Nodes.Single();

        copied.Parameters["Command"].Should().Be("git switch -c {branch}");
        copied.IsTraced.Should().BeTrue();
    }

    // A flow you have not read is not one that should already be running.
    [Fact]
    public void ACopy_IsNeverArmed_HoweverTheOriginalCame()
    {
        var source = new Workflow { Id = "w", Name = "Flow", IsActive = true };

        WorkflowCopy.Of(source, "Flow").IsActive.Should().BeFalse();
    }

    [Fact]
    public void ATemplateFlow_CarriesTheStepsWhereItsAuthorPutThem()
    {
        // A template lays its steps out left to right; the copy keeps that, or the flow opens as a heap.
        var source = new Workflow { Id = "w", Name = "Flow" };
        source.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start", X = 80, Y = 160 });
        source.Nodes.Add(new WorkflowNode { Id = "b", TypeId = "cockpit.notify", Name = "Tell me", X = 360, Y = 160 });

        var copy = WorkflowCopy.Of(source, "Flow");

        copy.Nodes.Select(node => (node.X, node.Y)).Should().Equal((80, 160), (360, 160));
    }

    // A hand-edited or truncated file can name a step that is not there. Dropping that wire beats carrying it into a
    // flow that would fail to run for reasons nobody could see on the canvas.
    [Fact]
    public void AWireToAStepThatIsNotInTheFlow_IsDropped()
    {
        var source = new Workflow { Id = "w", Name = "Flow" };
        source.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start" });
        source.Connections.Add(new WorkflowConnection { FromNodeId = "a", FromOutput = 0, ToNodeId = "ghost" });

        WorkflowCopy.Of(source, "Flow").Connections.Should().BeEmpty();
    }
}
