namespace Cockpit.Plugin.SessionReview;

// The uncommitted diff of a working directory (AC-50): whether git could read it, the branch, and the diff text.
internal sealed record GitDiffResult(bool Available, string Branch, string Diff)
{
    // Not a repo, no git, or the read was cancelled.
    public static readonly GitDiffResult Unavailable = new(false, string.Empty, string.Empty);

    // True when git read a repo that has uncommitted changes to show.
    public bool HasChanges => Available && !string.IsNullOrWhiteSpace(Diff);
}
