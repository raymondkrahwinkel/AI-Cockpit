using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// The switch: one value, a way out per case — <c>{state}</c> against "In Progress, Review, Done". Where the If has
/// two branches and a condition, this has as many branches as there are things the value can be, which is what a
/// ticket status, an exit code or a provider name actually is.
/// <para>
/// Matching is case-insensitive and ignores surrounding space: "review" and " Review " are the same status, and a
/// flow that fell through to "otherwise" over a capital letter would be a flow nobody could debug by looking.
/// </para>
/// <para>
/// A value that matches nothing leaves by "otherwise" rather than failing the step: not recognising a value is not
/// an error — it is the case the operator did not name, and the pin is there to catch exactly that. A <em>missing</em>
/// value is different: <c>{state}</c> when nothing called state came in is a step configured against data that is not
/// there, and that fails, like every other unresolved reference in this engine.
/// </para>
/// </summary>
internal sealed class SwitchRunner : IStepRunner
{
    public string TypeId => SwitchCases.TypeId;

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var value = context.Node.Parameters.GetValueOrDefault(SwitchCases.ValueParameter);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("This switch has no value to look at. Open it and write one, e.g. {state}.");
        }

        var cases = SwitchCases.Parse(context.Node.Parameters.GetValueOrDefault(SwitchCases.CasesParameter));
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("This switch has no cases, so every value would leave by \"otherwise\". Open it and name the ones you care about, e.g. In Progress, Review, Done.");
        }

        var resolved = context.Resolve(value);
        if (resolved.Missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"This switch looks at {string.Join(", ", resolved.Missing.Select(field => $"{{{field}}}"))}, and nothing by that name came in.");
        }

        var actual = resolved.Text.Trim();
        var match = cases.FirstOrDefault(name => string.Equals(name, actual, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(new StepOutcome(context.Input, match ?? SwitchCases.Otherwise));
    }
}
