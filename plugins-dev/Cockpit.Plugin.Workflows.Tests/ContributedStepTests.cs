using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// A step another plugin contributed (#69). The contract's whole promise is that its author writes no workflow code:
/// they declare what they ask for and do the work, and the engine deals in placeholders, items and branches on their
/// behalf. These tests hold that promise open.
/// </summary>
public class ContributedStepTests
{
    [Fact]
    public async Task ItsParameters_ArriveResolved_SoItsAuthorNeverLearnsThePlaceholderSyntaxExists()
    {
        var step = new FakeStep();
        var node = _Node(("Ticket", "{ticket}"));

        await new ContributedStep(step).RunAsync(
            new StepContext(node, [WorkflowItem.Of("ticket", "WEB-14")], _Nothing),
            CancellationToken.None);

        Assert.Equal("WEB-14", step.Seen!.Parameter("Ticket"));
    }

    [Fact]
    public async Task WhatItProduces_FlowsOnAsItems()
    {
        var outcome = await new ContributedStep(new FakeStep()).RunAsync(
            new StepContext(_Node(("Ticket", "WEB-14")), [], _Nothing),
            CancellationToken.None);

        Assert.Single(outcome.Items);
        Assert.Equal("In Progress", outcome.Items[0].Json["state"]!.ToString());
        Assert.Equal("WEB-14 → In Progress", outcome.Output);
    }

    [Fact]
    public async Task AStepThatProducesNothing_PassesOnWhatCameIn_RatherThanEmptyingTheFlowBehindIt()
    {
        var incoming = new[] { WorkflowItem.Of("ticket", "WEB-14") };

        var outcome = await new ContributedStep(new SilentStep()).RunAsync(
            new StepContext(_Node(), incoming, _Nothing),
            CancellationToken.None);

        Assert.Equivalent(incoming, outcome.Items);
    }

    [Fact]
    public async Task ADecisionsBranch_IsHandedBackUntouched_BecauseTheEngineMatchesTheWireOnIt()
    {
        // A note appended to a branch name ("true (nothing called {x} came in)") matches no wire, and the flow then
        // stops dead on a step that succeeded — the kind of failure that looks like nothing at all.
        var node = _Node(("Ticket", "{missing}"));

        var outcome = await new ContributedStep(new BranchingStep()).RunAsync(
            new StepContext(node, [], _Nothing),
            CancellationToken.None);

        Assert.Equal("yes", outcome.Output);
    }

    [Fact]
    public void ItsDeclaration_BecomesAStepTheCanvasCanDraw()
    {
        var type = ContributedStep.Describe(new FakeStep());

        Assert.Equal("fake.step", type.Id);
        Assert.Equal(new[] { "Ticket" }, type.Parameters);
        Assert.Equal("YOUTRACK", type.Heading);
        Assert.Equal(WorkflowNodeKind.Action, type.Kind);
        Assert.Equal("In Progress", type.Produces["state"]);
    }

    [Fact]
    public void AStepWithSeveralWaysOut_IsADecision_WithoutSayingSo()
    {
        Assert.Equal(WorkflowNodeKind.Decision, ContributedStep.Describe(new BranchingStep()).Kind);
    }

    [Fact]
    public void AContributedTrigger_IsATrigger_SoNothingFlowsIntoIt()
    {
        var type = ContributedStep.Describe(new PickedTrigger());

        Assert.Equal(WorkflowNodeKind.Trigger, type.Kind);
        Assert.False(type.HasInput);
    }

    [Fact]
    public async Task AContributedTrigger_IsNeverRun_BecauseItIsFired()
    {
        // The engine seeds a run with what the plugin fired; calling RunAsync on a trigger would be asking a doorbell
        // to answer the door.
        IWorkflowStep trigger = new PickedTrigger();

        var run = async () => await trigger.RunAsync(new WorkflowStepContext(new Dictionary<string, string>(), []), CancellationToken.None);

        await Assert.ThrowsAsync<NotSupportedException>(run);
    }

    private sealed class PickedTrigger : IWorkflowStep
    {
        public string TypeId => "fake.picked";

        public string Name => "Ticket picked";

        public string Description => "Fires when a ticket is picked for a session.";

        public string Icon => "🎫";

        public string Category => "YouTrack";

        public bool IsTrigger => true;

        public IReadOnlyList<string> Parameters => [];
    }

    private static readonly Dictionary<string, IReadOnlyList<WorkflowItem>> _Nothing = new(StringComparer.OrdinalIgnoreCase);

    private static WorkflowNode _Node(params (string Name, string Value)[] parameters)
    {
        var node = new WorkflowNode { Id = "n", TypeId = "fake.step", Name = "Start a ticket" };
        foreach (var (name, value) in parameters)
        {
            node.Parameters[name] = value;
        }

        return node;
    }

    private sealed class FakeStep : IWorkflowStep
    {
        public WorkflowStepContext? Seen { get; private set; }

        public string TypeId => "fake.step";

        public string Name => "Start a ticket";

        public string Description => "Moves a ticket.";

        public string Icon => "▶";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket"];

        public IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string> { ["state"] = "In Progress" };

        public Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken)
        {
            Seen = context;

            return Task.FromResult(new WorkflowStepResult(
                [new Dictionary<string, string> { ["state"] = "In Progress" }],
                "WEB-14 → In Progress"));
        }
    }

    private sealed class SilentStep : IWorkflowStep
    {
        public string TypeId => "fake.silent";

        public string Name => "Comment";

        public string Description => "Says something somewhere else.";

        public string Icon => "💬";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => [];

        public Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowStepResult.Done("Commented."));
    }

    private sealed class BranchingStep : IWorkflowStep
    {
        public string TypeId => "fake.branching";

        public string Name => "Is it open?";

        public string Description => "Two ways on.";

        public string Icon => "⑂";

        public string Category => "YouTrack";

        public IReadOnlyList<string> Parameters => ["Ticket"];

        public IReadOnlyList<string> Outputs => ["yes", "no"];

        public Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkflowStepResult([], "it is open", Branch: "yes"));
    }
}
