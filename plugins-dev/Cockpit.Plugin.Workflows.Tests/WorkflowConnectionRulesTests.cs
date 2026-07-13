using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// What may be wired to what (#69). These tests are the record of a correction: an earlier version refused fan-out
/// (one way out feeding several steps) and loops, on the assumption that they were mistakes. They are not — in n8n
/// both are ordinary, and a loop with a decision as its stop condition is a normal thing to draw. So the tests
/// below assert that the editor <em>allows</em> them, and only refuses wires the engine could never follow.
/// </summary>
public class WorkflowConnectionRulesTests
{
    [Fact]
    public void Connect_FromOneStepToTheNext_IsAllowed()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        workflow.Connect(trigger.Id, 0, notify.Id).IsAllowed.Should().BeTrue();
        workflow.Connections.Should().ContainSingle();
    }

    [Fact]
    public void Connect_OneWayOutToSeveralSteps_IsAllowed_BecauseFanOutIsOrdinary()
    {
        var workflow = _Workflow(out var trigger, out var notify, out var delegateStep);

        workflow.Connect(trigger.Id, 0, notify.Id).IsAllowed.Should().BeTrue();
        workflow.Connect(trigger.Id, 0, delegateStep.Id).IsAllowed.Should().BeTrue();

        workflow.Connections.Should().HaveCount(2);
    }

    [Fact]
    public void Connect_BackToAnEarlierStep_IsAllowed_BecauseThatIsWhatALoopIs()
    {
        var workflow = _Workflow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);
        workflow.Connect(first.Id, 0, second.Id);

        // second -> first: a loop. With a decision as its stop condition this is a shape workflows genuinely have.
        workflow.Connect(second.Id, 0, first.Id).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Connect_SeveralStepsIntoOne_IsAllowed_BecauseThatIsAMerge()
    {
        var workflow = _Workflow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);

        workflow.Connect(first.Id, 0, second.Id).IsAllowed.Should().BeTrue();
        workflow.Connect(trigger.Id, 0, second.Id).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Connect_IntoATrigger_IsRefused_BecauseATriggerIsWhereAFlowBegins()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        var rule = workflow.Connect(notify.Id, 0, trigger.Id);

        rule.IsAllowed.Should().BeFalse();
        rule.Reason.Should().Contain("trigger");
        workflow.Connections.Should().BeEmpty();
    }

    [Fact]
    public void Connect_AStepToItself_IsRefused()
    {
        var workflow = _Workflow(out _, out var notify, out _);

        workflow.Connect(notify.Id, 0, notify.Id).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Connect_TheSameWireTwice_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);
        workflow.Connect(trigger.Id, 0, notify.Id);

        var rule = workflow.Connect(trigger.Id, 0, notify.Id);

        rule.IsAllowed.Should().BeFalse();
        rule.Reason.Should().Contain("already");
        workflow.Connections.Should().ContainSingle();
    }

    [Fact]
    public void Connect_ADecisionsTwoBranches_AreSeparateWaysOut()
    {
        var workflow = _Workflow(out var trigger, out var yes, out var no);
        var decision = _Node("d", "cockpit.if", "If");
        workflow.Nodes.Add(decision);
        workflow.Connect(trigger.Id, 0, decision.Id);

        workflow.Connect(decision.Id, 0, yes.Id).IsAllowed.Should().BeTrue();
        workflow.Connect(decision.Id, 1, no.Id).IsAllowed.Should().BeTrue();

        decision.Outputs.Should().Equal("true", "false");
    }

    [Fact]
    public void Connect_FromAWayOutThatDoesNotExist_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        // A trigger has one way out; index 1 is not one of them.
        workflow.Connect(trigger.Id, 1, notify.Id).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Remove_TakesTheWiresThatTouchedTheStepWithIt()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);
        workflow.Connect(trigger.Id, 0, notify.Id);

        workflow.Remove(notify.Id);

        workflow.Nodes.Should().NotContain(notify);
        workflow.Connections.Should().BeEmpty();
    }

    [Fact]
    public void HasConnectionFrom_IsWhatDecidesWhetherTheCanvasOffersAPlus()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        workflow.HasConnectionFrom(trigger.Id, 0).Should().BeFalse();
        workflow.Connect(trigger.Id, 0, notify.Id);
        workflow.HasConnectionFrom(trigger.Id, 0).Should().BeTrue();
    }

    private static WorkflowNode _Node(string id, string typeId, string name) =>
        new() { Id = id, TypeId = typeId, Name = name };

    private static Workflow _Workflow(out WorkflowNode trigger, out WorkflowNode notify, out WorkflowNode delegateStep)
    {
        trigger = _Node("t", "cockpit.event", "Event");
        notify = _Node("a", "cockpit.notify", "Notify");
        delegateStep = _Node("b", "cockpit.delegate", "Delegate");

        return new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, notify, delegateStep } };
    }
}
