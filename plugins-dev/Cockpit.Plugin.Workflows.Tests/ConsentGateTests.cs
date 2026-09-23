using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

// The consent gate (#AC-38): a step that acts with the operator's rights is put to them for Approve/Deny before it
// runs — unless they started the run themselves. What these hold is that a shell/egress/session step cannot run on an
// agent's say-so without the operator seeing the literal action and allowing it, and that the operator running a flow
// by hand is not made to approve their own action.
public class ConsentGateTests
{
    private static ICockpitHost _Host(ConsentOutcome outcome, out List<ConsentRequest> asked)
    {
        var requests = new List<ConsentRequest>();
        asked = requests;
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(requests.Add)).Returns(new ConsentDecision(outcome));
        return host;
    }

    private static Workflow _Flow(out WorkflowNode trigger)
    {
        trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Start" };
        var danger = new WorkflowNode { Id = "d", TypeId = "danger", Name = "Run a command" };
        var after = new WorkflowNode { Id = "a", TypeId = "cockpit.notify", Name = "After" };
        var flow = new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, danger, after } };
        flow.Connect(trigger.Id, 0, danger.Id);
        flow.Connect(danger.Id, 0, after.Id);
        return flow;
    }

    private static WorkflowEngine _Engine(ICockpitHost host, ConsentingRunner dangerous) =>
        new([new ManualTriggerRunner(), dangerous, new RecordingRunner("cockpit.notify")], host);

    [Fact]
    public async Task DangerousStep_FromMcpAgent_Approved_Runs_ShowingTheGroundTruth()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Approved, out var asked);
        var dangerous = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        await _Engine(host, dangerous).RunAsync(flow, trigger.Id, RunOrigin.McpAgent);

        Assert.Single(asked);
        Assert.Equal("do the dangerous thing: Run a command", asked[0].Action);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.True(dangerous.Ran);
    }

    [Fact]
    public async Task DangerousStep_FromMcpAgent_Denied_DoesNotRun_AndStopsTheBranch()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Denied, out _);
        var dangerous = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        var run = await _Engine(host, dangerous).RunAsync(flow, trigger.Id, RunOrigin.McpAgent);

        Assert.False(dangerous.Ran, "a denied step never runs");
        Assert.DoesNotContain(run.Steps, step => step.NodeId == "a");
        Assert.Equal(RunStatus.Skipped, run.Steps.Single(step => step.NodeId == "d").Status);
    }

    [Fact]
    public async Task DangerousStep_FromOperator_RunsWithoutAsking()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Denied, out var asked);   // would deny — but must not even be asked
        var dangerous = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        await _Engine(host, dangerous).RunAsync(flow, trigger.Id, RunOrigin.Operator);

        Assert.Empty(asked);
        Assert.True(dangerous.Ran);
    }

    [Fact]
    public async Task DangerousStep_FromTrigger_Asks_UnlessTheFlowIsRunUnattended()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Approved, out var asked);

        await _Engine(host, new ConsentingRunner("danger", ConsentRisk.Dangerous)).RunAsync(flow, trigger.Id, RunOrigin.Trigger);
        Assert.Single(asked);

        flow.RunUnattended = true;
        asked.Clear();
        var unattended = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        await _Engine(host, unattended).RunAsync(flow, trigger.Id, RunOrigin.Trigger);
        Assert.Empty(asked);
        Assert.True(unattended.Ran);
    }

    // AC-1360: "Ask me first" goes through the broker, so a chat channel can answer it too. A yes goes on, a no is
    // Skipped, and a question nobody answers is a no once its wait runs out — never a silent yes.
    [Theory]
    [InlineData(ConsentOutcome.Approved, "", RunStatus.Succeeded, true, "You approved: Deploy?")]
    [InlineData(ConsentOutcome.Denied, "", RunStatus.Skipped, false, "Not approved")]
    [InlineData(null, "0.001", RunStatus.Skipped, false, "Nobody answered within 0.001 minutes")]
    public async Task AskMeFirst_IsPutToTheBroker_AndNoAnswerInTimeIsANo(
        ConsentOutcome? answer, string wait, RunStatus expectedStatus, bool continues, string expectedText)
    {
        var host = Substitute.For<ICockpitHost>();
        var asked = new List<ConsentRequest>();
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add), Arg.Any<CancellationToken>())
            .Returns(call => _Broker(answer, call.Arg<CancellationToken>()));

        var trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Start" };
        var approve = new WorkflowNode
        {
            Id = "q",
            TypeId = "cockpit.approve",
            Name = "Ask me first",
            Parameters = { ["Question"] = "Deploy?", [ApproveRunner.TimeoutParameter] = wait },
        };
        var after = new WorkflowNode { Id = "a", TypeId = "cockpit.notify", Name = "After" };
        var flow = new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, approve, after }, RunUnattended = true };
        flow.Connect(trigger.Id, 0, approve.Id);
        flow.Connect(approve.Id, 0, after.Id);

        var run = await new WorkflowEngine([new ManualTriggerRunner(), new ApproveRunner(host), new RecordingRunner("cockpit.notify")], host)
            .RunAsync(flow, trigger.Id, RunOrigin.Trigger);

        var step = run.Steps.Single(s => s.NodeId == "q");
        Assert.Equal(expectedStatus, step.Status);
        Assert.Contains(expectedText, step.Output + step.Note, StringComparison.Ordinal);
        Assert.Equal(continues, run.Steps.Any(s => s.NodeId == "a"));
        Assert.Equal(("Deploy?", ConsentRisk.Dangerous, (string?)null), (asked[0].Action, asked[0].Risk, asked[0].Source.PaneId));
    }

    // The broker's own contract (ConsentService): an answer when there is one, and Denied once the token is cancelled.
    private static Task<ConsentDecision> _Broker(ConsentOutcome? answer, CancellationToken cancellationToken) =>
        answer is { } given
            ? Task.FromResult(new ConsentDecision(given))
            : Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => ConsentDecision.Denied, TaskScheduler.Default);
}

// A runner that declares it needs consent and records whether it actually ran — enough to prove the gate.
internal sealed class ConsentingRunner(string typeId, ConsentRisk risk) : IStepRunner
{
    public string TypeId => typeId;

    public bool Ran { get; private set; }

    public ConsentRisk? RequiredConsent => risk;

    public string ConsentAction(StepContext context) => $"do the dangerous thing: {context.Node.Name}";

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        Ran = true;
        return Task.FromResult(StepOutcome.Passing(context.Input, "did it"));
    }
}
