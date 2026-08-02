namespace Cockpit.Plugin.SessionReview;

// How a row of a parsed file diff should read (AC-50) — drives its colour and its band in the panel. A file header
// is no longer a row kind: `DiffParser` turns it into a `FileDiff` of its own, so all that
// is left inside a file is its hunks and their lines.
internal enum DiffLineKind
{
    // An added line — green text on a green band.
    Added,

    // A removed line — red text on a red band.
    Removed,

    // A hunk header (`@@ … @@`) — drawn as a separator rule, not as another line of text.
    Hunk,

    // An unchanged context line — default foreground, no band.
    Context,
}
