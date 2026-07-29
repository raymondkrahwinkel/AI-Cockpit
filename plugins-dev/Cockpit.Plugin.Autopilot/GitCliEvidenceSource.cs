namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotEvidenceSource"/> (AC-255): reads the run's own git worktree to find out what a step
/// changed. It only ever reads — no <c>add</c>, no <c>commit</c>, no <c>stash push</c> — because an observation that
/// mutates what it observes is not evidence, and because the coordinator's own safety commit is the one place work is
/// staged. Every git fault degrades to null (no evidence, so the CEO keeps inspecting) rather than failing the step.
/// </summary>
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

        // `stash create` writes a commit whose tree is the worktree as it stands, and writes it nowhere: it touches no
        // ref, no index and no file, so it is a read as far as the run is concerned. That is what pins work an earlier
        // step left uncommitted, so this step is not later credited with it. It prints nothing on a clean tree — there
        // is then nothing uncommitted to pin and HEAD already is the worktree.
        var snapshot = await GitCommandLine.RunAsync("git", ["stash", "create"], worktreePath, cancellationToken);
        var commit = snapshot.Ok ? snapshot.StdOut.Trim() : string.Empty;

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

        var untracked = await GitCommandLine.RunAsync("git", ["ls-files", "--others", "--exclude-standard"], worktreePath, cancellationToken);
        var newFiles = untracked.Ok
            ? _Lines(untracked.StdOut).Where(path => !mark.UntrackedFiles.Contains(path, StringComparer.Ordinal)).ToArray()
            : [];

        var cut = _CutLength(patch.StdOut);
        return new AutopilotWorktreeChange(
            _Lines(names.StdOut),
            newFiles,
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
