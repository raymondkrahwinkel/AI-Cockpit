using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

// What a step is given when it runs (#69): itself, the items handed to it, and what every step that already ran
// produced — keyed by its name, which is how a parameter reaches back past the step before it
// (`{Run a command.output}`). Two steps sharing a name is an ambiguity the operator made; the later one wins,
// and renaming is the cure.
//
// `Node`: The step being run.
// `Input`: What the step before handed over.
// `Produced`: What each earlier step produced, by name.
public sealed record StepContext(
    WorkflowNode Node,
    IReadOnlyList<WorkflowItem> Input,
    IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>> Produced)
{
    // Fills the placeholders in one of this step's parameters.
    public StepDataResult Resolve(string? text) => StepData.Resolve(text, Input, Produced);

    // Fills the placeholders, passing each substituted value through `escapeValue` first — the
    // command step uses this to shell-quote untrusted step data so it stays one inert argument (AC-39).
    public StepDataResult Resolve(string? text, Func<string, string> escapeValue) =>
        StepData.Resolve(text, Input, Produced, escapeValue);
}
