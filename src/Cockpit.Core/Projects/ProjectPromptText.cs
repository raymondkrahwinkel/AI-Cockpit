namespace Cockpit.Core.Projects;

// Folds text onto one line and strips the characters that make it read as something it is not. Shared between
// `ProjectInfoField.Tidied` and `Sessions.SessionStartDefaults`'s memory note because both
// are the same failure shape from the same trust layer: operator- or plugin-typed text about to be dropped into a
// session's standing instructions, where a hidden line break arrives as a fresh instruction of its own and an
// invisible mark can make a value show as something other than what it is. One guard in one place rather than two
// copies of it — two copies is two chances for them to drift apart, and the gap between them is exactly the case
// that mattered.
internal static class ProjectPromptText
{
    // Unicode's complete set of mandatory line breaks, not only CR and LF: a value pasted out of a web page or a PDF
    // can carry a vertical tab, a form feed, a NEL or a line/paragraph separator, and Avalonia's text layout breaks a
    // line on every one of them. The whole set rather than the ones seen so far, because the point is that no value
    // with a hard break in it ever reaches a wrapping text block — that combination never finishes measuring on
    // Avalonia 12.0.5 and allocates until the process is killed.
    private static readonly char[] _LineBreaks =
        [(char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x85, (char)0x2028, (char)0x2029];

    // Characters that change how text reads without being seen: the bidirectional overrides and isolates, and the
    // zero-width marks. They matter here more than in ordinary text because a value doing double duty — a link's
    // label and its target, or a place named in a system prompt — must not be able to disagree with itself about
    // what it says.
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
