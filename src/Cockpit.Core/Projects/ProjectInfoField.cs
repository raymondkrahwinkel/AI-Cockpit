namespace Cockpit.Core.Projects;

/// <summary>
/// One line of whatever else an operator wants to keep beside a project (AC-295): a label they chose and the value
/// under it — a repository link, the customer's website, a contact, a licence number. Named by the operator rather
/// than by the model on purpose: the cockpit cannot know which kinds of information a project needs, and a typed
/// field per kind means a code change for every new one.
/// <para>
/// A list of these rather than a dictionary, because the operator types the order and it is the order they read it
/// back in; and because two rows may honestly carry the same label (two contacts) where a dictionary key may not.
/// </para>
/// <para>
/// Both halves are stored and shown as plain text — this is reference material an operator reads back, not a place
/// for a credential. A secret belongs in a profile's environment variables, which the config encrypts and masks.
/// </para>
/// </summary>
/// <param name="Label">What the operator calls this — free text, shown before the value.</param>
/// <param name="Value">What it says. A single line: it is read at a glance beside a project, not written in.</param>
public sealed record ProjectInfoField(string Label, string Value)
{
    /// <summary>
    /// Unicode's complete set of mandatory line breaks, not only CR and LF: a value pasted out of a web page or a PDF
    /// can carry a vertical tab, a form feed, a NEL or a line/paragraph separator, and Avalonia's text layout breaks a
    /// line on every one of them. The whole set rather than the ones seen so far, because the point is that no value
    /// with a hard break in it ever reaches a wrapping text block — that combination never finishes measuring on
    /// Avalonia 12.0.5 and allocates until the process is killed.
    /// </summary>
    private static readonly char[] LineBreaks =
        [(char)0x0A, (char)0x0B, (char)0x0C, (char)0x0D, (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>
    /// Characters that change how text reads without being seen: the bidirectional overrides and isolates, and the
    /// zero-width marks. They matter here more than in ordinary text because a row's value is both the link's label
    /// and its target — the two must not be able to disagree about where a click goes.
    /// </summary>
    private static readonly char[] DeceptiveMarks =
    [
        (char)0x200B, (char)0x200C, (char)0x200D, (char)0x200E, (char)0x200F,
        (char)0x202A, (char)0x202B, (char)0x202C, (char)0x202D, (char)0x202E,
        (char)0x2066, (char)0x2067, (char)0x2068, (char)0x2069,
        (char)0xFEFF,
    ];

    /// <summary>
    /// Whether this row says nothing yet — an untouched row the editor added and the operator left alone. Dropped
    /// rather than saved: there is nothing in it to lose, and an empty row would come back as a blank line on the
    /// project's card.
    /// </summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(Value);

    /// <summary>
    /// Whether there is a label to draw above the value. Whitespace counts as none, which is what a surface needs and
    /// what <see cref="IsBlank"/> deliberately does not say: a row of spaces beside a filled value is still a row worth
    /// keeping, it just has nothing to put over it.
    /// </summary>
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    /// <summary>
    /// Whether <see cref="Value"/> is a link a viewer can follow, which is what decides whether a surface draws it
    /// as one. Only <c>http</c> and <c>https</c>: the same limit the views already put on opening a URL, so a value
    /// that happens to read as <c>file:</c> or a custom scheme stays text.
    /// </summary>
    public bool IsWebLink =>
        Uri.TryCreate(Value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// This row as it will be stored and shown: trimmed, on one line, and without the invisible marks that make text
    /// read as something it is not. Pasting out of a document brings line breaks the row cannot hold — and a wrapping
    /// text block over a value with newlines in it is what crashed the issue dialogs on Avalonia 12.0.5.
    /// </summary>
    public ProjectInfoField Tidied() => new(_Tidied(Label), _Tidied(Value));

    private static string _Tidied(string text) => _WithoutDeceptiveMarks(
        string.Join(' ', text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));

    private static string _WithoutDeceptiveMarks(string text) =>
        text.IndexOfAny(DeceptiveMarks) < 0
            ? text
            : new string([.. text.Where(character => !DeceptiveMarks.Contains(character))]);
}
