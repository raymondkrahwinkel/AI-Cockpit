namespace Cockpit.Plugin.SessionReview;

/// <summary>What happened to a file in the working tree (AC-578) — the glyph and badge the tree shows beside it.</summary>
internal enum FileChangeKind
{
    /// <summary>Edited in place.</summary>
    Modified,

    /// <summary>Added and staged.</summary>
    Added,

    /// <summary>Removed.</summary>
    Deleted,

    /// <summary>Moved or renamed.</summary>
    Renamed,

    /// <summary>Changed, but git will not produce text for it (binary, or too large to be worth drawing).</summary>
    Binary,
}

/// <summary>
/// One row of a file's diff: what kind of line it is, where it sits in the old and new file, and its text without the
/// leading <c>+</c>/<c>-</c>/space. The sign is dropped here because the panel draws it in its own column and because
/// the word-level comparison must line up the actual content, not content-plus-marker.
/// </summary>
/// <param name="Kind">Added, removed, context, or the hunk header itself.</param>
/// <param name="OldLine">Line number in the old file, or null for an added line and for hunk headers.</param>
/// <param name="NewLine">Line number in the new file, or null for a removed line and for hunk headers.</param>
/// <param name="Text">The line without its marker. For a hunk header this is the whole <c>@@ … @@ …</c> line.</param>
internal sealed record DiffRow(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>
/// One file's worth of parsed diff (AC-578). <see cref="Added"/> and <see cref="Removed"/> are counted once at
/// construction rather than on each read: the tree asks for them while drawing every node, and the panel is built
/// from these records each time a file is selected.
/// </summary>
internal sealed record FileDiff(string Path, FileChangeKind Kind, IReadOnlyList<DiffRow> Rows)
{
    /// <summary>Lines this file gained.</summary>
    public int Added { get; } = Rows.Count(r => r.Kind == DiffLineKind.Added);

    /// <summary>Lines this file lost.</summary>
    public int Removed { get; } = Rows.Count(r => r.Kind == DiffLineKind.Removed);

    /// <summary>The file's own name, without the directories the tree already shows above it.</summary>
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}
