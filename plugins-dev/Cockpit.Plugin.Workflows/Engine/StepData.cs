using System.Text.RegularExpressions;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// How a step uses the data of the steps before it (#69). Two forms, one pair of braces:
/// <list type="bullet">
///   <item><c>{output}</c> — a field of what the step immediately before handed over.</item>
///   <item><c>{Run a command.output}</c> — a field of what <em>any</em> earlier step produced, by its name.</item>
/// </list>
/// <para>
/// Deliberately not n8n's <c>{{ $json.x }}</c>, and deliberately not an expression language. n8n needs JavaScript
/// because its work <em>is</em> reshaping foreign JSON; ours is orchestration — run this, tell me, send that — and a
/// sandbox with timeouts and a second mental mode is a steep price for "put that value here". Where an expression
/// will genuinely pay is a decision's condition; that is the day to reach for one, behind its own marker, so plain
/// text stays plain text.
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
    public static StepDataResult Resolve(
        string? text,
        IReadOnlyList<WorkflowItem> input,
        IReadOnlyDictionary<string, IReadOnlyList<WorkflowItem>>? produced = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new StepDataResult(text ?? string.Empty, []);
        }

        var missing = new List<string>();

        var resolved = Placeholder().Replace(text, match =>
        {
            var reference = match.Groups[1].Value.Trim();

            if (_Lookup(reference, input, produced) is { } value)
            {
                return value;
            }

            missing.Add(reference);
            return match.Value;
        });

        return new StepDataResult(resolved, missing);
    }

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

    [GeneratedRegex(@"\{([^{}\r\n]+)\}")]
    private static partial Regex Placeholder();
}

/// <summary>The text with its placeholders filled, and the references that were asked for but not there.</summary>
public sealed record StepDataResult(string Text, IReadOnlyList<string> Missing);
