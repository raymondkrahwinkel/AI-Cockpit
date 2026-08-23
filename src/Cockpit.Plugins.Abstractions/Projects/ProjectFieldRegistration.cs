namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// A field a plugin adds to the project editor (AC-317): "which YouTrack project is this", "which repository".
/// The plugin says what the field is and where its choices come from; the host draws it and stores the answer.
/// </summary>
/// <remarks>
/// Declared rather than drawn, so a description survives the editor being restyled and a project stays linked
/// to a tracker not installed on this machine without the row disappearing. Deliberately narrower than a config
/// section — a plugin that needs a whole panel is a different contribution.
/// </remarks>
/// <param name="Key">
/// Stable, plugin-chosen name this field is stored under on the project — <c>youtrack.project</c>,
/// <c>github.repository</c>. Prefix it with your plugin so two plugins do not collide by accident, and never
/// change it: it is what already-linked projects are keyed by. Two plugins registering the same key is a
/// supported case, not a clash — it means they agree on what the value is (a repository is a repository), and
/// the first registration wins so either one alone still offers the field.
/// </param>
/// <param name="Title">
/// The field's label in the editor — "YouTrack project", "GitHub repository".
/// </param>
/// <param name="LoadOptionsAsync">
/// Fetches the choices. Runs off the UI thread while the editor is already open and usable, so it may reach the
/// network or shell out. Return an empty list when there is nothing to offer (no instance configured, no
/// credentials) and throw when the fetch failed — the two say different things to the operator, and a failure
/// reported as "no options" reads as "you have no repositories".
/// </param>
public sealed record ProjectFieldRegistration(
    string Key,
    string Title,
    Func<CancellationToken, Task<IReadOnlyList<ProjectFieldOption>>> LoadOptionsAsync)
{
    /// <summary>
    /// The line under the label saying what this is for, in the editor's own voice. Null for a field whose title
    /// says it all.
    /// </summary>
    public string? Hint { get; init; }

    /// <summary>
    /// What the empty box suggests — a shape, not an instruction: <c>owner/repo</c>. Null for none.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Whether the editor lets the operator link more than one identifier under this key (AC-884), each its own
    /// row with an add/remove control. False by default: a single row, unchanged from before this existed.
    /// </summary>
    public bool AllowsMultiple { get; init; }
}
