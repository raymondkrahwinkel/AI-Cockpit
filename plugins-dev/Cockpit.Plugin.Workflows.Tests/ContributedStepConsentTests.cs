using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workflows;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// A contributed step declares in its own code whether it needs consent (#AC-38), and the workflows plugin cannot
/// override it. What these hold: the declared risk maps straight through to the runtime gate; an undeclared non-trigger
/// step is left out of the engine rather than run ungated; and only a Dangerous step is kept from an agent's reach — a
/// LowRisk one stays agent-buildable and is gated at run time instead.
/// </summary>
public class ContributedStepConsentTests
{
    [Theory]
    [InlineData(WorkflowStepConsent.None, null)]
    [InlineData(WorkflowStepConsent.LowRisk, ConsentRisk.LowRisk)]
    [InlineData(WorkflowStepConsent.Dangerous, ConsentRisk.Dangerous)]
    public void DeclaredRisk_MapsStraightThroughToTheGate(WorkflowStepConsent declared, ConsentRisk? expected)
    {
        new ContributedStep(new FakeStep("x", declared)).RequiredConsent.Should().Be(expected);
    }

    [Fact]
    public void OnlyAnUndeclaredNonTriggerStep_IsUndeclared()
    {
        ContributedStep.IsUndeclared(new FakeStep("undeclared", consent: null)).Should().BeTrue();
        ContributedStep.IsUndeclared(new FakeStep("declared", WorkflowStepConsent.None)).Should().BeFalse("it declared it is safe");
        ContributedStep.IsUndeclared(new FakeStep("trigger", consent: null, isTrigger: true)).Should().BeFalse("a trigger is never run, so it needs no declaration");
    }

    [Fact]
    public async Task AnUndeclaredNonTriggerStep_IsLeftOutOfTheEngine_WhileADeclaredOneRuns()
    {
        var host = Substitute.For<ICockpitHost>();
        var engine = EngineFactory.Create(host,
        [
            new FakeStep("declared", WorkflowStepConsent.None),
            new FakeStep("undeclared", consent: null),
        ]);

        var trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.manual", Name = "Start" };
        var declared = new WorkflowNode { Id = "d", TypeId = "declared", Name = "Declared" };
        var undeclared = new WorkflowNode { Id = "u", TypeId = "undeclared", Name = "Undeclared" };
        var flow = new Workflow { Id = "w", Name = "Flow", Nodes = { trigger, declared, undeclared } };
        flow.Connect(trigger.Id, 0, declared.Id);
        flow.Connect(declared.Id, 0, undeclared.Id);

        var run = await engine.RunAsync(flow, trigger.Id, RunOrigin.Operator);

        run.Steps.Single(step => step.NodeId == "d").Status.Should().Be(RunStatus.Succeeded, "a declared step is loaded and runs");
        run.Steps.Single(step => step.NodeId == "u").Status.Should().Be(RunStatus.Skipped, "an undeclared non-trigger step is left out of the engine, so nothing runs it");
    }

    [Fact]
    public void OnlyADangerousStep_IsKeptFromAnAgent_ALowRiskOneStaysBuildableButGatedAtRuntime()
    {
        var engine = EngineFactory.Create(Substitute.For<ICockpitHost>(),
        [
            new FakeStep("low", WorkflowStepConsent.LowRisk),
            new FakeStep("danger", WorkflowStepConsent.Dangerous),
        ]);

        engine.ConsentRequiredTypeIds.Should().Contain("low").And.Contain("danger", "both are put to the operator at run time");
        engine.AgentForbiddenTypeIds.Should().Contain("danger").And.NotContain("low",
            "a LowRisk step is left agent-buildable; only a Dangerous one is the operator's to build and arm");
    }

    private sealed class FakeStep(string typeId, WorkflowStepConsent? consent, bool isTrigger = false) : IWorkflowStep
    {
        public string TypeId => typeId;

        public string Name => typeId;

        public string Description => string.Empty;

        public string Icon => "?";

        public string Category => "Test";

        public bool IsTrigger => isTrigger;

        public WorkflowStepConsent? RequiredConsent => consent;

        public IReadOnlyList<string> Parameters => [];

        public Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(WorkflowStepResult.Done("ran"));
    }
}
