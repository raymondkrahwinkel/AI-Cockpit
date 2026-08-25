namespace Cockpit.Plugin.Autopilot;

// The real `IAutopilotEvidenceSource` (AC-255): reads the run's own git worktree to find out what a step
// changed. Only ever reads — no `add`, `commit`, or `stash push` — because an observation that mutates what it
// observes is not evidence. Every git fault degrades to null (no evidence) rather than failing the step.
internal sealed class GitCliEvidenceSource : IAutopilotEvidenceSource
{
    // The validation turn is a prompt, not a report: a large refactor's full patch would crowd out the acceptance it is
    // meant to be judged against, and on a big enough diff cost more than the inspection it replaces. Cut it here and
    // say so in the turn — the CEO is told to read the files for the rest.
    private const int MaxPatchCharacters = 20_000;

    public async Task<AutopilotWorktreeMark?> MarkAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        // `stash create` records the worktree as-is into a commit stored nowhere (no ref, no index entry), which
        // pins work an earlier step left uncommitted so this step isn't later credited with it. The identity is
        // supplied only for this call, so a machine with none configured doesn't fail here, and it never reaches a visible commit.
        var snapshot = await GitCommandLine.RunAsync(
            "git",
            ["-c", "user.name=cockpit-autopilot", "-c", "user.email=autopilot@localhost", "stash", "create"],
            worktreePath,
            cancellationToken);

        if (!snapshot.Ok)
        {
            // Refused — an unborn repository, a conflicted merge, an object store it cannot write to. Falling back to
            // HEAD here would quietly reinstate the very thing the mark exists to prevent, and the CEO would never
            // learn the difference. Report that there is nothing to observe and let it inspect the files itself.
            return null;
        }

        // Nothing printed means a clean tree: there is no uncommitted work to pin, so HEAD already is the worktree.
        var commit = snapshot.StdOut.Trim();
        if (commit.Length == 0)
        {
            var head = await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
            commit = head.Ok ? head.StdOut.Trim() : string.Empty;
        }

        if (commit.Length == 0)
        {
            return null;
        }

        // A file git does not track is invisible to any commit, so the ones already lying here have to be remembered
        // by name or they read as this step's work for the rest of the run.
        var untracked = await GitCommandLine.RunAsync("git", ["ls-files", "--others", "--exclude-standard"], worktreePath, cancellationToken);
        return new AutopilotWorktreeMark(commit, untracked.Ok ? _Lines(untracked.StdOut) : []);
    }

    public async Task<AutopilotWorktreeChange?> CollectAsync(string worktreePath, AutopilotWorktreeMark mark, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || string.IsNullOrWhiteSpace(mark.Commit) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        // Diffing the mark against the working tree (rather than against HEAD) covers both halves of what a step leaves
        // behind: what it committed itself, and what it left sitting uncommitted. Step agents are briefed to commit, but
        // GitCliPrPublisher's own leftover-work safety commit already assumes they do not always.
        var names = await GitCommandLine.RunAsync("git", ["diff", "--name-only", mark.Commit], worktreePath, cancellationToken);
        if (!names.Ok)
        {
            return null;
        }

        var patch = await GitCommandLine.RunAsync("git", ["diff", mark.Commit], worktreePath, cancellationToken);
        if (!patch.Ok)
        {
            return null;
        }

        // AC-1037: which tree this observation is of. Without it the CEO is handed a diff and a test result it cannot
        // tie to any commit — exactly the gap a green suite from another branch walked through — so a HEAD git will
        // not name degrades to no evidence at all rather than to evidence that cannot say where it came from.
        var head = await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
        if (!head.Ok || head.StdOut.Trim().Length == 0)
        {
            return null;
        }

        var untracked = await GitCommandLine.RunAsync("git", ["ls-files", "--others", "--exclude-standard"], worktreePath, cancellationToken);
        var newFiles = untracked.Ok
            ? _Lines(untracked.StdOut).Where(path => !mark.UntrackedFiles.Contains(path, StringComparer.Ordinal)).ToArray()
            : [];

        // A file that was lying here untracked and that this step merely handed to git crosses into the tracked half
        // and reads as brand new in the diff. It stays in the change — staging it is real — but it is singled out, so
        // an earlier step's file is not silently read as this step's output.
        var changed = _Lines(names.StdOut);
        var staged = changed.Where(path => mark.UntrackedFiles.Contains(path, StringComparer.Ordinal)).ToArray();

        var cut = _CutLength(patch.StdOut);
        return new AutopilotWorktreeChange(
            changed,
            newFiles,
            staged,
            head.StdOut.Trim(),
            patch.StdOut[..cut],
            cut < patch.StdOut.Length);
    }

    // Never cut between the two halves of a surrogate pair: a lone surrogate is not valid text, and this string is
    // about to be serialized into a session turn by a host that is entitled to reject it.
    private static int _CutLength(string patch) =>
        patch.Length <= MaxPatchCharacters ? patch.Length
        : char.IsHighSurrogate(patch[MaxPatchCharacters - 1]) ? MaxPatchCharacters - 1
        : MaxPatchCharacters;

    private static IReadOnlyList<string> _Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
