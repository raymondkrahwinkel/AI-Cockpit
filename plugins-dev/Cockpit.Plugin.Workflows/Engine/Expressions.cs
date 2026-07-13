using Cockpit.Plugin.Workflows.Model;
using Jint;
using Jint.Runtime;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// The powerful half of how a step uses data (#69). <c>{output}</c> puts a value somewhere; <c>{= ... }</c> computes
/// one — <c>{= output.split('\n').length }</c>, <c>{= exitCode != '0' }</c> — and a decision's condition is nothing
/// but such an expression.
/// <para>
/// JavaScript, through Jint, because a condition is an expression and inventing a half-language for it is how you end
/// up with something that can compare but not count. It stays behind its own marker, so plain text remains plain
/// text: the easy case never pays for the hard one.
/// </para>
/// <para>
/// A flow runs a shell command if you ask it to, so the expression sandbox is not a security boundary and is not
/// dressed up as one. The limits it does carry are against mistakes, not malice: a runaway loop must not take the
/// cockpit with it, so an expression gets a second and a ceiling on statements, and then it fails and says so.
/// </para>
/// </summary>
internal static class Expressions
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Runs <paramref name="code"/> against the data of the run. Every field of the incoming item is a variable
    /// (<c>output</c>), and any earlier step is reachable by name (<c>step('Run a command').output</c>). Throws with
    /// a sentence the operator can act on: an expression that cannot be evaluated is a step that failed, not a step
    /// that quietly produced nothing.
    /// </summary>
    public static object? Evaluate(
        string code,
        IReadOnlyList<WorkflowItem> input,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>> produced)
    {
        using var engine = new Jint.Engine(options => options
            .TimeoutInterval(Limit)
            .MaxStatements(10_000)
            .LimitRecursion(64)
            .Strict());

        foreach (var (key, value) in input.FirstOrDefault()?.Json ?? [])
        {
            engine.SetValue(key, value?.ToString() ?? string.Empty);
        }

        // Reaching an earlier step by name: a name with a space in it is not a JavaScript identifier, so it is asked
        // for rather than declared.
        engine.SetValue("step", (string name) =>
        {
            var fields = produced.TryGetValue(name, out var items)
                ? items.FirstOrDefault()?.Json
                : null;

            if (fields is null)
            {
                throw new InvalidOperationException($"No step called '{name}' has run.");
            }

            return fields.ToDictionary(entry => entry.Key, entry => entry.Value?.ToString() ?? string.Empty);
        });

        try
        {
            return engine.Evaluate(code).ToObject();
        }
        catch (JavaScriptException exception)
        {
            throw new InvalidOperationException($"The expression failed: {exception.Message}");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"The expression ran longer than {Limit.TotalSeconds:0} second and was stopped.");
        }
        catch (StatementsCountOverflowException)
        {
            throw new InvalidOperationException("The expression did too much — a loop with no way out?");
        }
        catch (Exception exception) when (exception is not InvalidOperationException and not OperationCanceledException)
        {
            // Whatever the parser calls its failures is Jint's business, not this cockpit's: what matters is that a
            // condition nobody can read stops the step rather than quietly counting as false.
            throw new InvalidOperationException($"The expression could not be read: {exception.Message}");
        }
    }

    /// <summary>Whether an expression's result counts as true. Empty text, zero and "false" are false; everything else is not.</summary>
    public static bool IsTrue(object? value) => value switch
    {
        null => false,
        bool truth => truth,
        double number => number != 0,
        string text => text.Length > 0 && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };
}
