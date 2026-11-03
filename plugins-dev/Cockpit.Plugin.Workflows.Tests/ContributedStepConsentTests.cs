using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workflows;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

// A contributed step declares in its own code whether it needs consent (#AC-38), and the workflows plugin cannot
// override it. What these hold: the declared risk maps straight through to the runtime gate; an undeclared non-trigger
// step is left out of the engine rather than run ungated; and only a Dangerous step is kept from an agent's reach — a
// LowRisk one stays agent-buildable and is gated at run time instead.
public class ContributedStepConsentTests
{
    [Theory]
    [InlineData(WorkflowStepConsent.None, null)]
    [InlineData(WorkflowStepConsent.LowRisk, ConsentRisk.LowRisk)]
    [InlineData(WorkflowStepConsent.Dangerous, ConsentRisk.Dangerous)]
    public void DeclaredRisk_MapsStraightThroughToTheGate(WorkflowStepConsent declared, ConsentRisk? expected)
    {
        Assert.Equal(expected, new ContributedStep(new FakeStep("x", declared)).RequiredConsent);
    }

    [Fact]
    public void OnlyADangerousStep_IsKeptFromAnAgent_ALowRiskOneStaysBuildableButGatedAtRuntime()
    {
        var engine = EngineFactory.Create(Substitute.For<ICockpitHost>(),
        [
            new FakeStep("low", WorkflowStepConsent.LowRisk),
            new FakeStep("danger", WorkflowStepConsent.Dangerous),
        ]);

        Assert.Contains("low", engine.ConsentRequiredTypeIds);
        Assert.Contains("danger", engine.ConsentRequiredTypeIds);
        Assert.Contains("danger", engine.AgentForbiddenTypeIds);
        Assert.DoesNotContain("low", engine.AgentForbiddenTypeIds);
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
