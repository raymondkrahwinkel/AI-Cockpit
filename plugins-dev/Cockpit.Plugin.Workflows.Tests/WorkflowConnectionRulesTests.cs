using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The rules about what may be wired to what (#69). They live in the model rather than the canvas because a
/// connection the engine could never follow is worse than no connection: the canvas would then show a flow that
/// does not do what it looks like it does.
/// </summary>
public class WorkflowConnectionRulesTests
{
    [Fact]
    public void Connect_FromAnActionToTheNextOne_IsAllowed()
    {
        var workflow = _Workflow(out var trigger, out var action, out _);

        var rule = workflow.Connect(trigger.Id, 0, action.Id);

        rule.IsAllowed.Should().BeTrue();
        workflow.Connections.Should().ContainSingle();
    }

    [Fact]
    public void Connect_IntoATrigger_IsRefused_BecauseATriggerIsWhereAFlowBegins()
    {
        var workflow = _Workflow(out var trigger, out var action, out _);

        var rule = workflow.Connect(action.Id, 0, trigger.Id);

        rule.IsAllowed.Should().BeFalse();
        rule.Reason.Should().Contain("trigger");
        workflow.Connections.Should().BeEmpty();
    }

    [Fact]
    public void Connect_ANodeToItself_IsRefused()
    {
        var workflow = _Workflow(out _, out var action, out _);

        workflow.Connect(action.Id, 0, action.Id).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Connect_TwiceFromTheSameWayOut_IsRefused_BecauseAStepContinuesOneWayAtATime()
    {
        var workflow = _Workflow(out var trigger, out var action, out var second);
        workflow.Connect(trigger.Id, 0, action.Id);

        var rule = workflow.Connect(trigger.Id, 0, second.Id);

        rule.IsAllowed.Should().BeFalse();
        rule.Reason.Should().Contain("already continues");
    }

    [Fact]
    public void Connect_ThatWouldCloseALoop_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);
        workflow.Connect(first.Id, 0, second.Id);

        // second -> first would send the run back to a step it already passed.
        var rule = workflow.Connect(second.Id, 0, first.Id);

        rule.IsAllowed.Should().BeFalse();
        rule.Reason.Should().Contain("loop");
    }

    [Fact]
    public void Connect_ADecisionsTwoBranches_AreBothAllowed_BecauseTheyAreDifferentWaysOut()
    {
        var workflow = _Workflow(out var trigger, out var yes, out var no);
        var decision = new WorkflowNode { Id = "d", TypeId = "cockpit.if", Kind = WorkflowNodeKind.Decision, Title = "Decision" };
        workflow.Nodes.Add(decision);
        workflow.Connect(trigger.Id, 0, decision.Id);

        workflow.Connect(decision.Id, 0, yes.Id).IsAllowed.Should().BeTrue();
        workflow.Connect(decision.Id, 1, no.Id).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Connect_FromAWayOutThatDoesNotExist_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var action, out _);

        // An action has one way out; index 1 is not one of them.
        workflow.Connect(trigger.Id, 1, action.Id).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void Remove_TakesTheWiresThatTouchedTheNodeWithIt()
    {
        var workflow = _Workflow(out var trigger, out var action, out _);
        workflow.Connect(trigger.Id, 0, action.Id);

        workflow.Remove(action.Id);

        workflow.Nodes.Should().NotContain(action);
        workflow.Connections.Should().BeEmpty();
    }

    private static Workflow _Workflow(out WorkflowNode trigger, out WorkflowNode action, out WorkflowNode second)
    {
        trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.event", Kind = WorkflowNodeKind.Trigger, Title = "Trigger" };
        action = new WorkflowNode { Id = "a", TypeId = "cockpit.notify", Kind = WorkflowNodeKind.Action, Title = "Notify" };
        second = new WorkflowNode { Id = "b", TypeId = "cockpit.delegate", Kind = WorkflowNodeKind.Action, Title = "Delegate" };

        return new Workflow
        {
            Id = "w",
            Name = "Flow",
            Nodes = { trigger, action, second },
        };
    }
}
