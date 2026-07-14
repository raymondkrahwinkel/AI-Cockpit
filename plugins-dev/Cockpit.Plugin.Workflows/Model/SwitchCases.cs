namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// A switch's ways out are its own (#69): unlike every other node type, they are not fixed by the type but written
/// by the operator — "In Progress, Review, Done" is three ways out, and adding a fourth case adds a fourth pin. This
/// is the one place that turns what they wrote into that list, so the canvas, the engine and the rewiring all read
/// the same cases from the same text.
/// <para>
/// There is always an <see cref="Otherwise"/>, and it is always last. A value that matches none of the cases has to
/// go somewhere: without it a switch would swallow whatever it did not recognise, which is the quiet kind of wrong.
/// </para>
/// </summary>
public static class SwitchCases
{
    public const string TypeId = "cockpit.switch";

    /// <summary>The way out taken by a value that matched no case.</summary>
    public const string Otherwise = "otherwise";

    /// <summary>The parameter holding the value to match — an expression over the run's data, e.g. <c>{state}</c>.</summary>
    public const string ValueParameter = "Value";

    /// <summary>The parameter holding the cases: comma- or newline-separated, in the order their pins appear.</summary>
    public const string CasesParameter = "Cases";

    /// <summary>
    /// The cases as written, in order — trimmed, blanks dropped, duplicates dropped (two pins with the same label
    /// would be two wires the operator could not tell apart, and only the first could ever be taken).
    /// </summary>
    public static IReadOnlyList<string> Parse(string? cases)
    {
        if (string.IsNullOrWhiteSpace(cases))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return cases
            .Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => !string.Equals(value, Otherwise, StringComparison.OrdinalIgnoreCase) && seen.Add(value))
            .ToList();
    }

    /// <summary>What this switch's pins are called: a case each, then "otherwise".</summary>
    public static IReadOnlyList<string> Outputs(IReadOnlyDictionary<string, string> parameters) =>
        [.. Parse(parameters.GetValueOrDefault(CasesParameter)), Otherwise];
}
