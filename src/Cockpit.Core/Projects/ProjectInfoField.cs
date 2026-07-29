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
/// A row is plain text unless the operator marks it a secret (AC-318): then the value is stored encrypted, masked
/// wherever a project is shown, and kept out of anything a session is told.
/// </para>
/// </summary>
/// <param name="Label">What the operator calls this — free text, shown before the value.</param>
/// <param name="Value">What it says. A single line: it is read at a glance beside a project, not written in.</param>
public sealed record ProjectInfoField(string Label, string Value)
{
    /// <summary>
    /// Whether a session started on this project is told this row (AC-314), off unless the operator says so. Off by
    /// default on purpose: these rows arrived as reference material for the operator to read (AC-295), so sending
    /// every one of them into a system prompt would change what already-entered rows do without anyone asking for it
    /// — and a row costs prompt budget at every session start, which is worth deciding per row rather than in bulk.
    /// </summary>
    public bool IsSharedWithSessions { get; init; }

    /// <summary>
    /// Whether <see cref="Value"/> is a credential (AC-318). A secret row is stored encrypted, masked wherever a
    /// project is shown, never drawn as a followable link, and never told to a session — a token in a system prompt is
    /// the thing this flag exists to prevent.
    /// </summary>
    public bool IsSecret { get; init; }

    /// <summary>
    /// Whether this row may be told to a session: what the operator asked for, unless it holds a credential. The two
    /// are answered together here rather than left to each surface, so a row that is both cannot reach a prompt because
    /// one caller forgot to check the other flag.
    /// </summary>
    public bool ReachesSessions => IsSharedWithSessions && !IsSecret;

    /// <summary>
    /// Whether a surface may draw <see cref="Value"/> as it is. False for a secret, which is masked, and for a web
    /// address, which is drawn as a link instead.
    /// </summary>
    public bool ShowsPlainValue => !IsSecret && !IsWebLink;

    /// <summary>
    /// What a surface shows in place of a secret. A fixed width rather than one dot per character: the length of a
    /// credential is itself something worth not telling anyone reading over a shoulder.
    /// </summary>
    public const string Mask = "••••••••";

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
        !IsSecret &&
        Uri.TryCreate(Value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// This row as it will be stored and shown: trimmed, on one line, and without the invisible marks that make text
    /// read as something it is not. Pasting out of a document brings line breaks the row cannot hold — and a wrapping
    /// text block over a value with newlines in it is what crashed the issue dialogs on Avalonia 12.0.5.
    /// </summary>
    /// <remarks>
    /// A <c>with</c> rather than a fresh <c>new(Label, Value)</c>: the positional form carries only those two, so it
    /// silently dropped every other member — and this runs on load and on save, so a row the operator had ticked to
    /// share would have quietly unticked itself.
    /// </remarks>
    public ProjectInfoField Tidied() => this with
    {
        Label = ProjectPromptText.OneLine(Label),
        Value = ProjectPromptText.OneLine(Value),
    };
}
