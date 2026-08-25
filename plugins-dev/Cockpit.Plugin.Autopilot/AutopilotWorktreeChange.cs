namespace Cockpit.Plugin.Autopilot;

// What git itself reported changed in a run's worktree over one step (AC-255) — the raw observation, kept apart
// from the wording and spot-checks built on it. `AddedFromBeforeTheMark`: files staged this step whose
// *contents* are an earlier step's work. `HeadCommit` is not optional (AC-1037).
internal sealed record AutopilotWorktreeChange(
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> UntrackedFiles,
    IReadOnlyList<string> AddedFromBeforeTheMark,
    string HeadCommit,
    string Patch,
    bool Truncated)
{
    // Whether git saw nothing at all — no tracked change and no new file.
    public bool IsEmpty => FilesChanged.Count == 0 && UntrackedFiles.Count == 0;
}
