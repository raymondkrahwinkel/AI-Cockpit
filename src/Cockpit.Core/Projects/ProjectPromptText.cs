namespace Cockpit.Core.Projects;

// AC-1013: Folds text onto one line and strips characters that make it read as something it isn't. Shared by
// ProjectInfoField.Tidied and SessionStartDefaults' memory note — same trust layer, same failure shape — as one
// guard rather than two copies that could drift apart.
internal static class ProjectPromptText
{
    // AC-1013: Unicode's complete set of mandatory line breaks, not only CR/LF — a hard break reaching a
    // wrapping text block never finishes measuring on Avalonia 12.0.5 and allocates until the process is killed.
    private static readonly char[] _LineBreaks =
        [(char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x85, (char)0x2028, (char)0x2029];

    // AC-1013: Characters that change how text reads without being seen (bidi overrides/isolates, zero-width
    // marks) — a value doing double duty (link label/target, system-prompt text) must not disagree with itself.
    private static readonly char[] _DeceptiveMarks =
    [
        (char)0x200B, (char)0x200C, (char)0x200D, (char)0x200E, (char)0x200F,
        (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E,
        (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069,
        (char)0xFEFF,
    ];

    // `text` on one line and without the invisible marks — trimmed, hard breaks folded to spaces,
    // deceptive marks removed. Pasting out of a document brings line breaks a single-line field or sentence cannot
    // hold; letting one through into a system prompt reads as an instruction the operator never wrote.
    public static string OneLine(string text) => _WithoutDeceptiveMarks(
        string.Join(' ', text.Split(_LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    private static string _WithoutDeceptiveMarks(string text) =>
        text.IndexOfAny(_DeceptiveMarks) < 0
            ? text
            : new string([.. text.Where(character => !_DeceptiveMarks.Contains(character))]);
}
