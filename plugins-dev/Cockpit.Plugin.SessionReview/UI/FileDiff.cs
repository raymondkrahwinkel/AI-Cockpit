namespace Cockpit.Plugin.SessionReview;

// What happened to a file in the working tree (AC-578) — the glyph and badge the tree shows beside it.
internal enum FileChangeKind
{
    // Edited in place.
    Modified,

    // Added and staged.
    Added,

    // Removed.
    Deleted,

    // Moved or renamed.
    Renamed,

    // Changed, but git will not produce text for it (binary, or too large to be worth drawing).
    Binary,
}

// One row of a file's diff: its kind, its old/new line numbers (null where not applicable), and its text
// without the leading `+`/`-`/space — dropped so the word-level comparison lines up actual content, not
// content-plus-marker. A hunk header's Text is the whole `@@ … @@ …` line.
internal sealed record DiffRow(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

// One file's worth of parsed diff (AC-578). `Added` and `Removed` are counted once at
// construction rather than on each read: the tree asks for them while drawing every node, and the panel is built
// from these records each time a file is selected.
internal sealed record FileDiff(string Path, FileChangeKind Kind, IReadOnlyList<DiffRow> Rows)
{
    // Lines this file gained.
    public int Added { get; } = Rows.Count(r => r.Kind == DiffLineKind.Added);

    // Lines this file lost.
    public int Removed { get; } = Rows.Count(r => r.Kind == DiffLineKind.Removed);

    // The file's own name, without the directories the tree already shows above it.
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}
