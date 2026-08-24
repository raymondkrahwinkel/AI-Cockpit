namespace Cockpit.Core.Projects;

// AC-1013: One line of whatever else an operator wants to keep beside a project (AC-295) — operator-named
// label/value, list (not dictionary) for order and dupes. Marked secret (AC-318): encrypted, masked, never sent.
// `Label`: What the operator calls this. `Value`: what it says, one line, read at a glance.
public sealed record ProjectInfoField(string Label, string Value)
{
    // AC-1013: Whether a session is told this row (AC-314), off by default — these rows arrived as reference
    // material for the operator (AC-295), so sending them all would silently change existing rows' behavior.
    public bool IsSharedWithSessions { get; init; }

    // Whether `Value` is a credential (AC-318). A secret row is stored encrypted, masked wherever a
    // project is shown, never drawn as a followable link, and never told to a session — a token in a system prompt is
    // the thing this flag exists to prevent.
    public bool IsSecret { get; init; }

    // Whether this row may be told to a session: what the operator asked for, unless it holds a credential. The two
    // are answered together here rather than left to each surface, so a row that is both cannot reach a prompt because
    // one caller forgot to check the other flag.
    public bool ReachesSessions => IsSharedWithSessions && !IsSecret;

    // Whether a surface may draw `Value` as it is. False for a secret, which is masked, and for a web
    // address, which is drawn as a link instead.
    public bool ShowsPlainValue => !IsSecret && !IsWebLink;

    // What a surface shows in place of a secret. A fixed width rather than one dot per character: the length of a
    // credential is itself something worth not telling anyone reading over a shoulder.
    public const string Mask = "••••••••";

    // Whether this row says nothing yet — an untouched row the editor added and the operator left alone. Dropped
    // rather than saved: there is nothing in it to lose, and an empty row would come back as a blank line on the
    // project's card.
    public bool IsBlank => string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(Value);

    // Whether there is a label to draw above the value. Whitespace counts as none, which is what a surface needs and
    // what `IsBlank` deliberately does not say: a row of spaces beside a filled value is still a row worth
    // keeping, it just has nothing to put over it.
    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    // Whether `Value` is a link a viewer can follow, which is what decides whether a surface draws it
    // as one. Only `http` and `https`: the same limit the views already put on opening a URL, so a value
    // that happens to read as `file:` or a custom scheme stays text.
    public bool IsWebLink =>
        !IsSecret &&
        Uri.TryCreate(Value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // AC-1013: Trimmed to one line without invisible marks — a value with newlines crashed the issue dialogs
    // on Avalonia 12.0.5. Uses `with` rather than `new(Label, Value)`, whose positional form would
    // silently drop every other member (e.g. an operator's IsSharedWithSessions tick) on every load/save.
    public ProjectInfoField Tidied() => this with
    {
        Label = ProjectPromptText.OneLine(Label),
        Value = ProjectPromptText.OneLine(Value),
    };
}
