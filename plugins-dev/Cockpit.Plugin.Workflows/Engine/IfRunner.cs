namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// The decision: two ways on, and which one is taken is decided by an expression over the data of the run —
/// <c>exitCode != '0'</c>, <c>output.includes('error')</c>, <c>step('Run a command').output.length > 0</c>.
/// <para>
/// A condition that cannot be evaluated fails the step. The tempting alternative — treat an unreadable condition as
/// false and quietly take the other branch — is how a flow ends up reporting green while doing the wrong thing, and
/// there is no way to see it from the outside.
/// </para>
/// </summary>
internal sealed class IfRunner : IStepRunner
{
    public string TypeId => "cockpit.if";

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var condition = context.Node.Parameters.GetValueOrDefault("Condition");
        if (string.IsNullOrWhiteSpace(condition))
        {
            throw new InvalidOperationException("This decision has no condition. Open it and write one, e.g. exitCode != '0'.");
        }

        // The braces are optional here: a condition *is* an expression, so demanding {= ... } around it would be
        // ceremony. Writing them anyway is not an error — it is the same expression.
        var code = condition.Trim();
        if (code.StartsWith("{=", StringComparison.Ordinal) && code.EndsWith('}'))
        {
            code = code[2..^1];
        }

        var held = Expressions.IsTrue(Expressions.Evaluate(code, context.Input, context.Produced));

        // The branch is named in the outcome; the engine reads it and follows only that wire.
        return Task.FromResult(new StepOutcome(context.Input, held ? "true" : "false"));
    }
}
