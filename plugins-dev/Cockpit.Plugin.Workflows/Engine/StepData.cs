using System.Text.RegularExpressions;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// How a step uses the data of the steps before it (#69). Two forms, one pair of braces:
/// <list type="bullet">
///   <item><c>{output}</c> — a field of what the step immediately before handed over.</item>
///   <item><c>{Run a command.output}</c> — a field of what <em>any</em> earlier step produced, by its name.</item>
///   <item><c>{= output.split('\n').length }</c> — a computed value (see <see cref="Expressions"/>).</item>
/// </list>
/// <para>
/// Two paths on purpose. Naming a value is what you want nine times out of ten, and it should cost nothing to learn:
/// a field in braces, no language, no dollar signs. Computing one is the tenth time — and rather than a half-language
/// that can compare but not count, that is real JavaScript behind its own marker, so plain text stays plain text and
/// the easy case never pays for the hard one.
/// </para>
/// <para>
/// The one rule that is not negotiable: a field that was asked for but never arrived is left exactly as written and
/// reported, never quietly turned into an empty string. An empty path in the middle of a command is a worse outcome
/// than a command that visibly did not resolve.
/// </para>
/// </summary>
public static partial class StepData
{
    /// <summary>
    /// Fills the placeholders in <paramref name="text"/>. <paramref name="input"/> is what this step was handed;
    /// <paramref name="produced"/> is what every step that has already run produced, by name.
    /// </summary>
    /// <param name="escapeValue">
    /// Applied to each substituted value — a placeholder lookup or an expression result — before it is spliced in,
    /// leaving the surrounding template untouched. The command step passes shell quoting here so untrusted step data
    /// cannot break out of its argument (AC-39); callers that resolve into a non-shell context (a URL, a message, a
    /// working directory handed to an API) pass nothing and get the raw value.
    /// </param>
    public static StepDataResult Resolve(
        string? text,
        IReadOnlyList<WorkflowItem> input,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>>? produced = null,
        Func<string, string>? escapeValue = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new StepDataResult(text ?? string.Empty, []);
        }

        var escape = escapeValue ?? (static value => value);
        var missing = new List<string>();
        var errors = new List<string>();

        // Expressions first: what they compute may contain braces, and a placeholder pass over the result would then
        // try to read the computed text as a reference.
        var computed = Expression().Replace(text, match =>
        {
            try
            {
                // A computed value is derived from the same step data and spliced the same way, so it is escaped too;
                // a failed expression is left exactly as written (and reported), never escaped into place.
                return escape(_Text(Expressions.Evaluate(match.Groups[1].Value, input, produced ?? _nothing)));
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
                return match.Value;
            }
        });

        var resolved = Placeholder().Replace(computed, match =>
        {
            var reference = match.Groups[1].Value.Trim();

            if (_Lookup(reference, input, produced) is { } value)
            {
                return escape(value);
            }

            missing.Add(reference);
            return match.Value;
        });

        return new StepDataResult(resolved, missing, errors);
    }

    private static readonly Dictionary<string, IReadOnlyList<WorkflowItem>> _nothing = new(StringComparer.OrdinalIgnoreCase);

    // What an expression's result looks like as text. .NET writes a boolean as "True" and a whole number as "3"
    // only by luck of formatting; a value that came out of JavaScript should read back the way it was written there,
    // or a condition copied into a message says something the language it came from never said.
    private static string _Text(object? value) => value switch
    {
        null => string.Empty,
        bool truth => truth ? "true" : "false",
        double number when number == Math.Floor(number) && !double.IsInfinity(number) => ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture),
        double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The fields a step could refer to, given what it was handed — what the dialog lists so the operator does not have to guess the names.</summary>
    public static IReadOnlyList<string> FieldsOf(IReadOnlyList<WorkflowItem> items) =>
        items.FirstOrDefault()?.Json.Select(field => field.Key).ToList() ?? [];

    // A reference is either a plain field of the incoming item, or a named step's field. The split is on the last
    // dot, and a name that no step carries is not silently read as a field: it is missing, and says so.
    private static string? _Lookup(
        string reference,
        IReadOnlyList<WorkflowItem> input,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>>? produced)
    {
        var dot = reference.LastIndexOf('.');

        if (dot > 0 && produced is not null)
        {
            var name = reference[..dot].Trim();
            var field = reference[(dot + 1)..].Trim();

            if (produced.TryGetValue(name, out var items))
            {
                return items.FirstOrDefault()?.Json[field]?.ToString();
            }
        }

        return input.FirstOrDefault()?.Json[reference]?.ToString();
    }

    [GeneratedRegex(@"\{=([^{}]*)\}")]
    private static partial Regex Expression();

    [GeneratedRegex(@"\{([^{}=\r\n][^{}\r\n]*)\}")]
    private static partial Regex Placeholder();
}

/// <summary>The text with its placeholders filled, the references that were asked for but not there, and the expressions that would not run.</summary>
public sealed record StepDataResult(string Text, IReadOnlyList<string> Missing, IReadOnlyList<string> Errors)
{
    public StepDataResult(string text, IReadOnlyList<string> missing) : this(text, missing, []) { }
}
