using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// What may be wired to what (#69). These tests are the record of a correction: an earlier version refused fan-out
// (one way out feeding several steps) and loops, on the assumption that they were mistakes. They are not — in n8n
// both are ordinary, and a loop with a decision as its stop condition is a normal thing to draw. So the tests
// below assert that the editor *allows* them, and only refuses wires the engine could never follow.
public class WorkflowConnectionRulesTests
{
    [Fact]
    public void Connect_FromOneStepToTheNext_IsAllowed()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        Assert.True(workflow.Connect(trigger.Id, 0, notify.Id).IsAllowed);
        Assert.Single(workflow.Connections);
    }

    [Fact]
    public void Connect_OneWayOutToSeveralSteps_IsAllowed_BecauseFanOutIsOrdinary()
    {
        var workflow = _Workflow(out var trigger, out var notify, out var delegateStep);

        Assert.True(workflow.Connect(trigger.Id, 0, notify.Id).IsAllowed);
        Assert.True(workflow.Connect(trigger.Id, 0, delegateStep.Id).IsAllowed);

        Assert.Equal(2, System.Linq.Enumerable.Count(workflow.Connections));
    }

    [Fact]
    public void Connect_BackToAnEarlierStep_IsAllowed_BecauseThatIsWhatALoopIs()
    {
        var workflow = _Workflow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);
        workflow.Connect(first.Id, 0, second.Id);

        // second -> first: a loop. With a decision as its stop condition this is a shape workflows genuinely have.
        Assert.True(workflow.Connect(second.Id, 0, first.Id).IsAllowed);
    }

    [Fact]
    public void Connect_SeveralStepsIntoOne_IsAllowed_BecauseThatIsAMerge()
    {
        var workflow = _Workflow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);

        Assert.True(workflow.Connect(first.Id, 0, second.Id).IsAllowed);
        Assert.True(workflow.Connect(trigger.Id, 0, second.Id).IsAllowed);
    }

    [Fact]
    public void Connect_IntoATrigger_IsRefused_BecauseATriggerIsWhereAFlowBegins()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        var rule = workflow.Connect(notify.Id, 0, trigger.Id);

        Assert.False(rule.IsAllowed);
        Assert.Contains("trigger", rule.Reason);
        Assert.Empty(workflow.Connections);
    }

    [Fact]
    public void Connect_AStepToItself_IsRefused()
    {
        var workflow = _Workflow(out _, out var notify, out _);

        Assert.False(workflow.Connect(notify.Id, 0, notify.Id).IsAllowed);
    }

    [Fact]
    public void Connect_TheSameWireTwice_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);
        workflow.Connect(trigger.Id, 0, notify.Id);

        var rule = workflow.Connect(trigger.Id, 0, notify.Id);

        Assert.False(rule.IsAllowed);
        Assert.Contains("already", rule.Reason);
        Assert.Single(workflow.Connections);
    }

    [Fact]
    public void Connect_ADecisionsTwoBranches_AreSeparateWaysOut()
    {
        var workflow = _Workflow(out var trigger, out var yes, out var no);
        var decision = _Node("d", "cockpit.if", "If");
        workflow.Nodes.Add(decision);
        workflow.Connect(trigger.Id, 0, decision.Id);

        Assert.True(workflow.Connect(decision.Id, 0, yes.Id).IsAllowed);
        Assert.True(workflow.Connect(decision.Id, 1, no.Id).IsAllowed);

        Assert.Equal(new[] { "true", "false" }, decision.Outputs);
    }

    [Fact]
    public void Connect_FromAWayOutThatDoesNotExist_IsRefused()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        // A trigger has one way out; index 1 is not one of them.
        Assert.False(workflow.Connect(trigger.Id, 1, notify.Id).IsAllowed);
    }

    [Fact]
    public void Remove_TakesTheWiresThatTouchedTheStepWithIt()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);
        workflow.Connect(trigger.Id, 0, notify.Id);

        workflow.Remove(notify.Id);

        Assert.DoesNotContain(notify, workflow.Nodes);
        Assert.Empty(workflow.Connections);
    }

    [Fact]
    public void HasConnectionFrom_IsWhatDecidesWhetherTheCanvasOffersAPlus()
    {
        var workflow = _Workflow(out var trigger, out var notify, out _);

        Assert.False(workflow.HasConnectionFrom(trigger.Id, 0));
        workflow.Connect(trigger.Id, 0, notify.Id);
        Assert.True(workflow.HasConnectionFrom(trigger.Id, 0));
    }

    private static WorkflowNode _Node(string id, string typeId, string name) =>
        new() { Id = id, TypeId = typeId, Name = name };

    private static Workflow _Workflow(out WorkflowNode trigger, out WorkflowNode notify, out WorkflowNode delegateStep)
    {
        trigger = _Node("t", "cockpit.text-match", "Event");
        notify = _Node("a", "cockpit.notify", "Notify");
        delegateStep = _Node("b", "cockpit.delegate", "Delegate");

        return new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, notify, delegateStep } };
    }
}
