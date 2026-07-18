namespace Cockpit.Plugin.SessionReview;

/// <summary>How a line of unified-diff output should read (AC-50) — drives its colour in the panel.</summary>
internal enum DiffLineKind
{
    /// <summary>An added line (<c>+…</c>) — green.</summary>
    Added,

    /// <summary>A removed line (<c>-…</c>) — red.</summary>
    Removed,

    /// <summary>A hunk header (<c>@@ … @@</c>) — accent.</summary>
    Hunk,

    /// <summary>A file header (<c>diff --git</c>, <c>+++</c>, <c>---</c>, <c>index …</c>, <c>new file …</c>) — emphasised.</summary>
    FileHeader,

    /// <summary>An unchanged context line — default foreground.</summary>
    Context,
}
