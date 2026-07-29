namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotEvidenceSource"/> (AC-255): reads the run's own git worktree to find out what a step
/// changed. It only ever reads — no <c>add</c>, no <c>commit</c>, no <c>stash</c> — because an observation that mutates
/// what it observes is not evidence, and because the coordinator's own safety commit is the one place work is staged.
/// Every git fault degrades to null (no evidence, so the CEO keeps inspecting) rather than failing the step.
/// </summary>
internal sealed class GitCliEvidenceSource : IAutopilotEvidenceSource
{
    // The validation turn is a prompt, not a report: a large refactor's full patch would crowd out the acceptance it is
    // meant to be judged against, and on a big enough diff cost more than the inspection it replaces. Cut it here and
    // say so in the turn — the CEO is told to read the files for the rest.
    private const int MaxPatchCharacters = 20_000;

    public async Task<string?> MarkAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        var head = await GitCommandLine.RunAsync("git", ["rev-parse", "HEAD"], worktreePath, cancellationToken);
        var commit = head.StdOut.Trim();
        return head.Ok && commit.Length > 0 ? commit : null;
    }

    public async Task<AutopilotWorktreeChange?> CollectAsync(string worktreePath, string sinceCommit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || string.IsNullOrWhiteSpace(sinceCommit) || !Directory.Exists(worktreePath))
        {
            return null;
        }

        // Diffing the mark against the working tree (rather than against HEAD) covers both halves of what a step
        // leaves behind: what it committed itself, and what it left sitting uncommitted. Step agents are briefed to
        // commit, but GitCliPrPublisher's own leftover-work safety commit already assumes they do not always.
        var names = await GitCommandLine.RunAsync("git", ["diff", "--name-only", sinceCommit], worktreePath, cancellationToken);
        if (!names.Ok)
        {
            return null;
        }

        var patch = await GitCommandLine.RunAsync("git", ["diff", sinceCommit], worktreePath, cancellationToken);
        if (!patch.Ok)
        {
            return null;
        }

        // A brand-new file the step never added is invisible to any diff, so ask git for those separately — otherwise
        // a step that wrote three new files reads as "changed nothing" and the spot-check below fires on a lie.
        var untracked = await GitCommandLine.RunAsync("git", ["ls-files", "--others", "--exclude-standard"], worktreePath, cancellationToken);

        var truncated = patch.StdOut.Length > MaxPatchCharacters;
        return new AutopilotWorktreeChange(
            _Lines(names.StdOut),
            untracked.Ok ? _Lines(untracked.StdOut) : [],
            truncated ? patch.StdOut[..MaxPatchCharacters] : patch.StdOut,
            truncated);
    }

    private static IReadOnlyList<string> _Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
