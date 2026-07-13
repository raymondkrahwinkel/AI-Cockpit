using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The engine (#69): what runs, in what order, and what each step is handed. Tested with runners that do nothing
/// but record — the engine knows the shape of a flow and nothing about what a step means, and that is exactly what
/// makes it testable without a cockpit.
/// </summary>
public class WorkflowEngineTests
{
    [Fact]
    public async Task ARun_FollowsTheWires_AndHandsEachStepWhatTheOneBeforeItProduced()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);
        workflow.Connect(first.Id, 0, second.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), recorder]).RunAsync(workflow, trigger.Id);

        run.Status.Should().Be(RunStatus.Succeeded);
        run.Steps.Select(step => step.NodeName).Should().Equal("Start", "First", "Second");

        // The trigger's item reaches the first step, and the first step's output reaches the second.
        recorder.Inputs.Should().HaveCount(2);
        recorder.Inputs[1].Single().Json["from"]!.ToString().Should().Be("First");
    }

    [Fact]
    public async Task FanOut_RunsBothBranches()
    {
        var workflow = _Flow(out var trigger, out var left, out var right);
        workflow.Connect(trigger.Id, 0, left.Id);
        workflow.Connect(trigger.Id, 0, right.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), new RecordingRunner("cockpit.notify")])
            .RunAsync(workflow, trigger.Id);

        run.Steps.Select(step => step.NodeName).Should().BeEquivalentTo(["Start", "First", "Second"]);
        run.Status.Should().Be(RunStatus.Succeeded);
    }

    [Fact]
    public async Task AStepThisBuildCannotRun_IsSkippedWithAReason_NotCountedAsSuccess()
    {
        // A flow that reports green while doing nothing is the worst thing this could be.
        var workflow = _Flow(out var trigger, out var unknown, out _);
        workflow.Connect(trigger.Id, 0, unknown.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner()]).RunAsync(workflow, trigger.Id);

        var step = run.Steps.Single(step => step.NodeId == unknown.Id);
        step.Status.Should().Be(RunStatus.Skipped);
        step.Note.Should().Contain("cannot run");
    }

    [Fact]
    public async Task ASwitchedOffStep_IsPassedBy_AndTheItemsFlowStraightThroughIt()
    {
        var recorder = new RecordingRunner("cockpit.notify");
        var workflow = _Flow(out var trigger, out var skipped, out var after);
        skipped.IsDisabled = true;
        workflow.Connect(trigger.Id, 0, skipped.Id);
        workflow.Connect(skipped.Id, 0, after.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), recorder]).RunAsync(workflow, trigger.Id);

        run.Steps.Single(step => step.NodeId == skipped.Id).Status.Should().Be(RunStatus.Skipped);

        // The step after it still ran, on the data it would have had.
        recorder.Inputs.Should().ContainSingle();
        recorder.Inputs[0].Single().Json["startedBy"]!.ToString().Should().Be("you");
    }

    [Fact]
    public async Task AFailingStep_StopsItsBranch_AndTheRunSaysWhy()
    {
        var workflow = _Flow(out var trigger, out var failing, out var after);
        workflow.Connect(trigger.Id, 0, failing.Id);
        workflow.Connect(failing.Id, 0, after.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), new ThrowingRunner("cockpit.notify")])
            .RunAsync(workflow, trigger.Id);

        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().Contain("no message");
        run.Steps.Single(step => step.NodeId == failing.Id).Status.Should().Be(RunStatus.Failed);
        run.Steps.Should().NotContain(step => step.NodeId == after.Id, "the branch stops where it broke");
    }

    [Fact]
    public async Task ALoopWithNoWayOut_FailsLoudly_RatherThanHangingTheCockpit()
    {
        var workflow = _Flow(out var trigger, out var first, out var second);
        workflow.Connect(trigger.Id, 0, first.Id);
        workflow.Connect(first.Id, 0, second.Id);
        workflow.Connect(second.Id, 0, first.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), new RecordingRunner("cockpit.notify")])
            .RunAsync(workflow, trigger.Id);

        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().Contain("loop");
        run.Steps.Count.Should().BeLessThanOrEqualTo(WorkflowEngine.MaxSteps + 1);
    }

    [Fact]
    public async Task ADecision_TakesTheBranchItsRunnerNames()
    {
        var workflow = new Workflow { Id = "w", Name = "Flow" };
        var trigger = _Node("t", "cockpit.manual", "Start");
        var decision = _Node("d", "cockpit.if", "If");
        var yes = _Node("y", "cockpit.notify", "Yes");
        var no = _Node("n", "cockpit.notify", "No");
        workflow.Nodes.AddRange([trigger, decision, yes, no]);

        workflow.Connect(trigger.Id, 0, decision.Id);
        workflow.Connect(decision.Id, 0, yes.Id);
        workflow.Connect(decision.Id, 1, no.Id);

        var run = await new WorkflowEngine([
                new ManualTriggerRunner(),
                new BranchingRunner("cockpit.if", "false"),
                new RecordingRunner("cockpit.notify"),
            ])
            .RunAsync(workflow, trigger.Id);

        run.Steps.Select(step => step.NodeName).Should().Equal("Start", "If", "No");
    }

    [Fact]
    public async Task ATriggerWiredToNothing_FailsRatherThanReportingGreenForZeroWork()
    {
        // Pressing Execute on a trigger that leads nowhere used to produce a green run of one step — which reads
        // exactly like a flow that worked.
        var workflow = new Workflow { Id = "w", Name = "Flow" };
        var trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Run manually" };
        workflow.Nodes.Add(trigger);

        var run = await new WorkflowEngine([new ManualTriggerRunner()]).RunAsync(workflow, "t");

        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().Contain("wired to nothing");
    }

    [Fact]
    public async Task RunningFromAStepThatIsNotInTheFlow_FailsRatherThanDoingNothingQuietly()
    {
        var run = await new WorkflowEngine([]).RunAsync(_Flow(out _, out _, out _), "nope");

        run.Status.Should().Be(RunStatus.Failed);
        run.Error.Should().NotBeNullOrEmpty();
    }

    private static WorkflowNode _Node(string id, string typeId, string name) =>
        new() { Id = id, TypeId = typeId, Name = name };

    private static Workflow _Flow(out WorkflowNode trigger, out WorkflowNode first, out WorkflowNode second)
    {
        trigger = _Node("t", "cockpit.manual", "Start");
        first = _Node("a", "cockpit.notify", "First");
        second = _Node("b", "cockpit.notify", "Second");

        return new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, first, second } };
    }
}

/// <summary>A runner that records what it was handed and passes on a marker saying it ran — enough to prove order and data flow.</summary>
internal sealed class RecordingRunner(string typeId) : IStepRunner
{
    public string TypeId => typeId;

    public List<IReadOnlyList<WorkflowItem>> Inputs { get; } = [];

    public Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken)
    {
        Inputs.Add(input);
        return Task.FromResult(new StepOutcome([WorkflowItem.Of("from", node.Name)], $"ran {node.Name}"));
    }
}

/// <summary>A runner that fails the way a real one does: with a sentence the operator can act on.</summary>
internal sealed class ThrowingRunner(string typeId) : IStepRunner
{
    public string TypeId => typeId;

    public Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This step has no message to send.");
}

/// <summary>A decision that always takes the branch it was told to.</summary>
internal sealed class BranchingRunner(string typeId, string branch) : IStepRunner
{
    public string TypeId => typeId;

    public Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken) =>
        Task.FromResult(new StepOutcome(input, branch));
}
