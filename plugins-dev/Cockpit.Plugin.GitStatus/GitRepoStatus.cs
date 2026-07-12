namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// The git status of one configured repository (#1): its <see cref="Branch"/>, the number of
/// <see cref="Uncommitted"/> working-tree changes (staged + unstaged + untracked), and how far it is
/// <see cref="Ahead"/>/<see cref="Behind"/> its upstream (only meaningful when <see cref="HasUpstream"/>).
/// <see cref="Error"/> carries a message when the path is not a git repo or git could not be run, in which
/// case the count fields are zero. Grid-friendly string columns are derived by the dialog.
/// </summary>
public sealed record GitRepoStatus(
    string Path,
    string Name,
    string Branch,
    int Uncommitted,
    int Ahead,
    int Behind,
    bool HasUpstream,
    string? Error)
{
    /// <summary>True when there is nothing to commit and nothing to push (and no error) — the "all clean" case.</summary>
    public bool IsClean => Error is null && Uncommitted == 0 && Ahead == 0;

    /// <summary>The ahead/behind column for the dialog grid, e.g. "↑2 ↓1", "up to date", "no upstream" or "—".</summary>
    public string RemoteText => GitStatusSummary.RemoteState(this);

    /// <summary>A short state glyph + word for the grid: "✓ clean", "● changes" or "⚠ error".</summary>
    public string StateText => Error is not null ? "⚠ error" : IsClean ? "✓ clean" : "● changes";
}
