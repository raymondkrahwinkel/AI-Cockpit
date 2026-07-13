using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// A step another plugin contributed (<see cref="IWorkflowStep"/>), seen from inside the engine: a type for the
/// picker and a runner for the run, built from the one declaration. This is the whole of what the workflows plugin
/// knows about YouTrack — that someone offers a step called <c>youtrack.start</c> which asks for a ticket id.
/// <para>
/// Parameters are resolved before the step ever sees them, so a contributed step gets <c>{output}</c> for free and
/// its author never learns the syntax exists. That is the point of resolving in one place.
/// </para>
/// </summary>
internal sealed class ContributedStep(IWorkflowStep step) : IStepRunner
{
    public string TypeId => step.TypeId;

    /// <summary>How the picker and the canvas see it.</summary>
    public static NodeTypeDescriptor Describe(IWorkflowStep step) => new(
        step.TypeId,
        step.Name,
        step.Description,
        step.Icon,
        // A contributed step is an action or a decision by the shape of its ways out — a plugin does not get to say
        // it is a trigger, because nothing in this build would ever fire it.
        NodeCategory.External,
        step.Outputs.Count > 1 ? WorkflowNodeKind.Decision : WorkflowNodeKind.Action,
        step.Outputs,
        step.Parameters,
        step.Produces.Count > 0 ? step.Produces : null,
        step.Category.ToUpperInvariant());

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var missing = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in step.Parameters)
        {
            var resolved = context.Resolve(context.Node.Parameters.GetValueOrDefault(name));
            parameters[name] = resolved.Text;
            missing.AddRange(resolved.Missing);
        }

        var result = await step.RunAsync(new WorkflowStepContext(parameters, _Items(context.Input)), cancellationToken);

        // Empty means "pass on what came in": a step that only *does* something should not empty the flow behind it.
        var items = result.Items.Count == 0
            ? context.Input
            : result.Items.Select(item => WorkflowItem.Of(item)).ToList();

        // A decision's outcome *is* the name of the way out it took — the engine reads it to pick the wire, so
        // nothing may be appended to it. Anything else says what happened, and may.
        if (result.Branch is { } branch)
        {
            return new StepOutcome(items, branch);
        }

        var note = missing.Count > 0
            ? $" (nothing called {string.Join(", ", missing.Select(field => $"{{{field}}}"))} came in)"
            : string.Empty;

        return new StepOutcome(items, result.Output + note);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> _Items(IReadOnlyList<WorkflowItem> items) =>
        items
            .Select(item => (IReadOnlyDictionary<string, string>)item.Json.ToDictionary(
                field => field.Key,
                field => field.Value?.ToString() ?? string.Empty,
                StringComparer.Ordinal))
            .ToList();
}
