using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// What a step is given when it runs (#69): itself, the items handed to it, and what every step that already ran
/// produced — keyed by its name, which is how a parameter reaches back past the step before it
/// (<c>{Run a command.output}</c>). Two steps sharing a name is an ambiguity the operator made; the later one wins,
/// and renaming is the cure.
/// </summary>
/// <param name="Node">The step being run.</param>
/// <param name="Input">What the step before handed over.</param>
/// <param name="Produced">What each earlier step produced, by name.</param>
public sealed record StepContext(
    WorkflowNode Node,
    IReadOnlyList<WorkflowItem> Input,
    IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>> Produced)
{
    /// <summary>Fills the placeholders in one of this step's parameters.</summary>
    public StepDataResult Resolve(string? text) => StepData.Resolve(text, Input, Produced);
}
