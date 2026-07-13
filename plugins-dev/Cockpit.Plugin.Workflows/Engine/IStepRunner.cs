using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// What one kind of step actually does (#69). The engine knows the shape of a flow; a runner knows what "notify"
/// or "run a command" means. Keeping them apart is what lets a plugin one day contribute a step type — and what
/// lets the engine be tested with runners that do nothing but record that they were called.
/// </summary>
public interface IStepRunner
{
    /// <summary>The type this runs, e.g. <c>cockpit.notify</c>.</summary>
    string TypeId { get; }

    /// <summary>
    /// Runs the step on what it was handed, and returns what to hand on. Throwing means the step failed: the engine
    /// records why and stops that branch.
    /// </summary>
    Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken);
}

/// <summary>What a step produced: the items the next step gets, and a line for the run log saying what happened.</summary>
/// <param name="Items">What flows on. Usually the input, or what the step made.</param>
/// <param name="Output">What the operator reads in the run: the command's output, the message that was sent.</param>
public sealed record StepOutcome(IReadOnlyList<WorkflowItem> Items, string Output)
{
    public static StepOutcome Passing(IReadOnlyList<WorkflowItem> input, string output) => new(input, output);
}
