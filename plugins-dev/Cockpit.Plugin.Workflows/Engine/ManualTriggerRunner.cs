using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>The manual trigger: you started it, so there is nothing to do but hand the flow its first item.</summary>
internal sealed class ManualTriggerRunner : IStepRunner
{
    public string TypeId => "cockpit.manual";

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new StepOutcome([WorkflowItem.Of("startedBy", "you")], "Started by hand."));
}
