using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The consent gate (#AC-38): a step that acts with the operator's rights is put to them for Approve/Deny before it
/// runs — unless they started the run themselves. What these hold is that a shell/egress/session step cannot run on an
/// agent's say-so without the operator seeing the literal action and allowing it, and that the operator running a flow
/// by hand is not made to approve their own action.
/// </summary>
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

        asked.Should().ContainSingle();
        asked[0].Action.Should().Be("do the dangerous thing: Run a command", "the literal action is shown, not a summary");
        asked[0].Risk.Should().Be(ConsentRisk.Dangerous);
        dangerous.Ran.Should().BeTrue();
    }

    [Fact]
    public async Task DangerousStep_FromMcpAgent_Denied_DoesNotRun_AndStopsTheBranch()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Denied, out _);
        var dangerous = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        var run = await _Engine(host, dangerous).RunAsync(flow, trigger.Id, RunOrigin.McpAgent);

        dangerous.Ran.Should().BeFalse("a denied step never runs");
        run.Steps.Should().NotContain(step => step.NodeId == "a", "the branch stops where consent was refused");
        run.Steps.Single(step => step.NodeId == "d").Status.Should().Be(RunStatus.Skipped);
    }

    [Fact]
    public async Task DangerousStep_FromOperator_RunsWithoutAsking()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Denied, out var asked);   // would deny — but must not even be asked
        var dangerous = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        await _Engine(host, dangerous).RunAsync(flow, trigger.Id, RunOrigin.Operator);

        asked.Should().BeEmpty("the operator starting the run is the consent");
        dangerous.Ran.Should().BeTrue();
    }

    [Fact]
    public async Task DangerousStep_FromTrigger_Asks_UnlessTheFlowIsRunUnattended()
    {
        var flow = _Flow(out var trigger);
        var host = _Host(ConsentOutcome.Approved, out var asked);

        await _Engine(host, new ConsentingRunner("danger", ConsentRisk.Dangerous)).RunAsync(flow, trigger.Id, RunOrigin.Trigger);
        asked.Should().ContainSingle("a trigger fire asks by default");

        flow.RunUnattended = true;
        asked.Clear();
        var unattended = new ConsentingRunner("danger", ConsentRisk.Dangerous);

        await _Engine(host, unattended).RunAsync(flow, trigger.Id, RunOrigin.Trigger);
        asked.Should().BeEmpty("the operator marked the flow run-unattended");
        unattended.Ran.Should().BeTrue();
    }
}

/// <summary>A runner that declares it needs consent and records whether it actually ran — enough to prove the gate.</summary>
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
