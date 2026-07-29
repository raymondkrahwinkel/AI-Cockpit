namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where a worktree stood at one moment (AC-255) — what a step's own change is later measured against.
/// <para>
/// It is deliberately not just a commit. Nothing commits between ordinary steps, so a mark that only remembered
/// <c>HEAD</c> would carry every earlier step's uncommitted work into the next step's evidence: a step that changed
/// nothing would be shown its predecessor's diff, and the "reported work but the worktree is unchanged" spot-check
/// would stay silent because the diff was not empty. Both halves of the worktree therefore have to be pinned — the
/// tracked contents through <see cref="Commit"/>, the files git does not track through <see cref="UntrackedFiles"/>.
/// </para>
/// </summary>
/// <param name="Commit">
/// A commit whose tree is the worktree exactly as it stood — the snapshot <c>git stash create</c> writes without
/// touching the worktree, the index or any ref, falling back to <c>HEAD</c> when there was nothing uncommitted to pin.
/// </param>
/// <param name="UntrackedFiles">The files git already did not track at this moment, so they are not counted as the step's own.</param>
internal sealed record AutopilotWorktreeMark(string Commit, IReadOnlyList<string> UntrackedFiles);
