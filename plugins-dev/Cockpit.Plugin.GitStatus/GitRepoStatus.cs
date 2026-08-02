namespace Cockpit.Plugin.GitStatus;

// The git status of one configured repository (#1): its `Branch`, the number of
// `Uncommitted` working-tree changes (staged + unstaged + untracked), and how far it is
// `Ahead`/`Behind` its upstream (only meaningful when `HasUpstream`).
// `Error` carries a message when the path is not a git repo or git could not be run, in which
// case the count fields are zero. Grid-friendly string columns are derived by the dialog.
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
    // True when there is nothing to commit and nothing to push (and no error) — the "all clean" case.
    public bool IsClean => Error is null && Uncommitted == 0 && Ahead == 0;

    // The ahead/behind column for the dialog grid, e.g. "↑2 ↓1", "up to date", "no upstream" or "—".
    public string RemoteText => GitStatusSummary.RemoteState(this);

    // A short state word for the grid: "clean", "changes" or "error" — paired with a coloured icon in the dialog.
    public string StateText => Error is not null ? "error" : IsClean ? "clean" : "changes";
}
