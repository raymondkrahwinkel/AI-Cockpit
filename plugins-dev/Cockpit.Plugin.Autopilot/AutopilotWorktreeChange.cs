namespace Cockpit.Plugin.Autopilot;

// What git itself reported changed in a run's worktree over one step (AC-255) — the raw observation, kept apart from
// the wording and the spot-checks built on it so collecting and judging are testable separately.
//
// `FilesChanged`: Paths git listed as changed against the step's starting commit, relative to the worktree.
// `UntrackedFiles`:
// New files sitting in the worktree that git does not track yet. Listed separately because a diff cannot show them:
// a step that wrote a file without adding it has done real work the patch below is silent about.
// `AddedFromBeforeTheMark`:
// Files this step handed to git that were already lying in the worktree untracked when it started. They belong in
// `FilesChanged` — staging a file is a real change to the repository — but their *contents* are an
// earlier step's work, and a diff shows them as brand new. Kept apart so the CEO is told, rather than left to read a
// file someone else wrote as this step's output.
// `Patch`: The diff as git printed it, cut to what a brief can carry when `Truncated`.
// `Truncated`:
// The diff was longer than the brief carries and was cut. Said out loud in the validation turn — a silently shortened
// diff would read as the whole change and is exactly the kind of quiet degradation this gate exists to avoid.
internal sealed record AutopilotWorktreeChange(
    IReadOnlyList<string> FilesChanged,
    IReadOnlyList<string> UntrackedFiles,
    IReadOnlyList<string> AddedFromBeforeTheMark,
    string Patch,
    bool Truncated)
{
    // Whether git saw nothing at all — no tracked change and no new file.
    public bool IsEmpty => FilesChanged.Count == 0 && UntrackedFiles.Count == 0;
}
