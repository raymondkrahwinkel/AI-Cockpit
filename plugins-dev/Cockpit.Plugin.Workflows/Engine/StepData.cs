using System.Text.RegularExpressions;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// How a step uses what the step before it produced (#69). Write <c>{output}</c> in any field and the value the
/// previous step handed over is put in its place.
/// <para>
/// Deliberately not n8n's <c>{{ $json.x }}</c>: this is the cockpit's own, and it is one thing — a field name in
/// braces. A workflow tool that needs an expression language before it can send you a message has made the simple
/// case pay for the hard one. When a field is genuinely missing, it is left as-is and the run says so, rather than
/// quietly becoming an empty string in the middle of your command.
/// </para>
/// </summary>
public static partial class StepData
{
    /// <summary>Fills <c>{field}</c> placeholders from the first item the step was handed. Unknown fields are reported, not silently emptied.</summary>
    public static StepDataResult Resolve(string? text, IReadOnlyList<WorkflowItem> items)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new StepDataResult(text ?? string.Empty, []);
        }

        var json = items.FirstOrDefault()?.Json;
        var missing = new List<string>();

        var resolved = Placeholder().Replace(text, match =>
        {
            var field = match.Groups[1].Value.Trim();
            var value = json?[field];

            if (value is null)
            {
                missing.Add(field);
                return match.Value;
            }

            return value.ToString();
        });

        return new StepDataResult(resolved, missing);
    }

    /// <summary>The fields a step could refer to, given what it was handed — what the settings panel lists so the operator does not have to guess the names.</summary>
    public static IReadOnlyList<string> FieldsOf(IReadOnlyList<WorkflowItem> items) =>
        items.FirstOrDefault()?.Json.Select(field => field.Key).ToList() ?? [];

    [GeneratedRegex(@"\{([A-Za-z0-9_ .-]+)\}")]
    private static partial Regex Placeholder();
}

/// <summary>The text with its placeholders filled, and the fields that were asked for but not there.</summary>
public sealed record StepDataResult(string Text, IReadOnlyList<string> Missing);
