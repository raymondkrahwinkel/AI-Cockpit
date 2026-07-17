using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// What happens when a step fails (#69). Until now the answer was "the branch stops and the run is failed", which is
/// right when nobody said otherwise and useless when they did: a push that fails should be able to tell Slack, and a
/// notification nobody received should not stop a deploy that worked.
/// <para>
/// Three answers, in the order the operator meant them: a wire from the step's error way out (the failure goes
/// somewhere, and the run is one that <em>handled</em> a failure rather than one that failed), "keep going" (carry on
/// down the ordinary wire), and — failing both — stop, because a flow that quietly walked past a step that did not work
/// would be worse than one that stopped.
/// </para>
/// </summary>
public class ErrorPathTests
{
    [Fact]
    public async Task AFailureWithAnErrorWire_GoesDownIt_AndCarriesWhatWentWrong()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var failing, out var handler);
        _Wire(workflow, trigger, 0, failing);
        _Wire(workflow, failing, failing.ErrorOutput, handler);

        var run = await _Engine(recorder, failing.TypeId).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        // The handler ran, and it was handed the failure as data — {error} in a Slack message says more than "a step
        // failed" ever will.
        run.Steps.Should().Contain(step => step.NodeId == handler.Id);
        recorder.Inputs[^1][0].Json["error"]!.ToString().Should().Contain("no message");
        recorder.Inputs[^1][0].Json["step"]!.ToString().Should().Be(failing.Name);
    }

    [Fact]
    public async Task AHandledFailure_IsNotAFailedRun()
    {
        // The distinction is the whole point of an error path: the run did what it was told, including about the thing
        // that went wrong.
        var workflow = _Flow(out var trigger, out var failing, out var handler);
        _Wire(workflow, trigger, 0, failing);
        _Wire(workflow, failing, failing.ErrorOutput, handler);

        var run = await _Engine(new RecordingRunner("cockpit.notify"), failing.TypeId).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        run.Status.Should().Be(RunStatus.Succeeded);
        run.Steps.Single(step => step.NodeId == failing.Id).Status.Should().Be(RunStatus.Failed, "what failed is still in the run");
        run.Steps.Single(step => step.NodeId == failing.Id).Note.Should().Contain("handled by the error path");
    }

    [Fact]
    public async Task KeepGoing_CarriesOnDownTheOrdinaryWire()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var failing, out var after);
        failing.ContinueOnError = true;
        _Wire(workflow, trigger, 0, failing);
        _Wire(workflow, failing, 0, after);

        var run = await _Engine(recorder, failing.TypeId).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        run.Steps.Should().Contain(step => step.NodeId == after.Id);
        run.Status.Should().Be(RunStatus.Succeeded);
        run.Steps.Single(step => step.NodeId == failing.Id).Note.Should().Contain("carry on");
    }

    [Fact]
    public async Task AnErrorWire_WinsOverKeepGoing_BecauseDrawingItSaidWhereTheFailureShouldGo()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var failing, out var handler);
        failing.ContinueOnError = true;
        _Wire(workflow, trigger, 0, failing);
        _Wire(workflow, failing, failing.ErrorOutput, handler);

        await _Engine(recorder, failing.TypeId).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        recorder.Inputs[^1][0].Json["error"].Should().NotBeNull("the failure went down the wire, not past it");
    }

    [Fact]
    public async Task WithNeither_TheBranchStopsAndTheRunSaysWhy()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var failing, out var after);
        _Wire(workflow, trigger, 0, failing);
        _Wire(workflow, failing, 0, after);

        var run = await _Engine(recorder, failing.TypeId).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        run.Status.Should().Be(RunStatus.Failed);
        run.Steps.Should().NotContain(step => step.NodeId == after.Id, "a flow that walked past a step that did not work would be worse than one that stopped");
    }

    [Fact]
    public async Task AStepThatSucceeds_NeverLeavesByItsErrorWay()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var working, out var handler);
        _Wire(workflow, trigger, 0, working);
        _Wire(workflow, working, working.ErrorOutput, handler);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), recorder], Substitute.For<ICockpitHost>()).RunAsync(workflow, trigger.Id, RunOrigin.Operator);

        run.Steps.Should().NotContain(step => step.NodeId == handler.Id);
    }

    private static WorkflowEngine _Engine(RecordingRunner recorder, string failingTypeId) =>
        new([new ManualTriggerRunner(), new ThrowingRunner(failingTypeId), recorder], Substitute.For<ICockpitHost>());

    private static Workflow _Flow(out WorkflowNode trigger, out WorkflowNode second, out WorkflowNode third)
    {
        trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Start" };
        second = new WorkflowNode { Id = "a", TypeId = "cockpit.command", Name = "Push" };
        third = new WorkflowNode { Id = "b", TypeId = "cockpit.notify", Name = "Tell Slack" };

        return new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, second, third } };
    }

    private static void _Wire(Workflow workflow, WorkflowNode from, int output, WorkflowNode to) =>
        workflow.Connections.Add(new WorkflowConnection { FromNodeId = from.Id, FromOutput = output, ToNodeId = to.Id });
}
