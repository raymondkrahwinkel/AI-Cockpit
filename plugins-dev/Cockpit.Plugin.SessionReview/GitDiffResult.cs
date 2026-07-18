namespace Cockpit.Plugin.SessionReview;

/// <summary>The uncommitted diff of a working directory (AC-50): whether git could read it, the branch, and the diff text.</summary>
internal sealed record GitDiffResult(bool Available, string Branch, string Diff)
{
    /// <summary>Not a repo, no git, or the read was cancelled.</summary>
    public static readonly GitDiffResult Unavailable = new(false, string.Empty, string.Empty);

    /// <summary>True when git read a repo that has uncommitted changes to show.</summary>
    public bool HasChanges => Available && !string.IsNullOrWhiteSpace(Diff);
}
