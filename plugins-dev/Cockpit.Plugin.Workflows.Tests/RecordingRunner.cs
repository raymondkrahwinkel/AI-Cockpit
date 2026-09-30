using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// A runner that records what it was handed and passes on a marker saying it ran — enough to prove order and data flow.
internal sealed class RecordingRunner(string typeId) : IStepRunner
{
    public string TypeId => typeId;

    public List<IReadOnlyList<WorkflowItem>> Inputs { get; } = [];

    // What each step could reach by name at the moment it ran — the engine's promise that a parameter can look further back than one step.
    public List<IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>>> Reachable { get; } = [];

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        Inputs.Add(context.Input);
        Reachable.Add(context.Produced.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase));

        return Task.FromResult(new StepOutcome([WorkflowItem.Of("from", context.Node.Name)], $"ran {context.Node.Name}"));
    }
}

// A runner that fails the way a real one does: with a sentence the operator can act on.
internal sealed class ThrowingRunner(string typeId) : IStepRunner
{
    public string TypeId => typeId;

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("This step has no message to send.");
}

// A decision that always takes the branch it was told to.
internal sealed class BranchingRunner(string typeId, string branch) : IStepRunner
{
    public string TypeId => typeId;

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new StepOutcome(context.Input, branch));
}
