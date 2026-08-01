namespace Cockpit.Plugin.SessionReview;

/// <summary>
/// How a row of a parsed file diff should read (AC-50) — drives its colour and its band in the panel. A file header
/// is no longer a row kind: <see cref="DiffParser"/> turns it into a <see cref="FileDiff"/> of its own, so all that
/// is left inside a file is its hunks and their lines.
/// </summary>
internal enum DiffLineKind
{
    /// <summary>An added line — green text on a green band.</summary>
    Added,

    /// <summary>A removed line — red text on a red band.</summary>
    Removed,

    /// <summary>A hunk header (<c>@@ … @@</c>) — drawn as a separator rule, not as another line of text.</summary>
    Hunk,

    /// <summary>An unchanged context line — default foreground, no band.</summary>
    Context,
}
