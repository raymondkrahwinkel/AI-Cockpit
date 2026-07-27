using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Worktrees;

/// <summary>
/// Brings the branch a worktree is about to fork from up to date first (AC-349). Without this the fork base is
/// whatever <c>git rev-parse HEAD</c> happens to say in the operator's checkout, which is only as recent as the last
/// time they pulled — a session then starts tens of commits behind and every measurement made against the
/// remote-tracking refs (how much work a worktree still holds) is as stale as the refs themselves.
/// <para>
/// The operator's own working tree is touched as little as possible: only a fast-forward, only when the branch has
/// nothing of its own, nothing uncommitted, and nothing on disk the update would write over. Every case this declines
/// to handle — and every error along the way — forks from the local HEAD instead and says so, because a session
/// starting on an older base is a nuisance and losing someone's work is not. The one state that is not simply
/// "untouched" is a fast-forward cut short by its own guard, which git may have half applied; that is detected
/// afterwards and named, rather than reported as if nothing had happened.
/// </para>
/// </summary>
internal static class WorktreeSourceUpdater
{
    // Well short of GitCli's default hang guard: this runs on the session-start path, and the whole point is that a
    // network that is slow or gone delays a session by seconds rather than minutes. The fallback is a fork from the
    // local HEAD, which is exactly what happened before this existed.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    // The merge is local, but it runs the repository's post-merge hook, and a hook that installs packages can take
    // as long as it likes. Bounded so a session start cannot hang on someone else's hook.
    private static readonly TimeSpan MergeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many colliding paths to name before the message stops listing them.</summary>
    private const int NamedCollisions = 3;

    /// <param name="mergeTimeout">Test seam: shorten the guard on the fast-forward so a killed merge can be driven deliberately.</param>
    public static async Task<WorktreeSourceRefresh> BringUpToDateAsync(
        GitRepositoryInfo repository,
        CancellationToken cancellationToken,
        TimeSpan? mergeTimeout = null)
    {
        if (repository.CurrentBranch is not { } branch)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.DetachedHead);
        }

        try
        {
            return await _UpdateAsync(repository.Root, branch, mergeTimeout ?? MergeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A git that timed out, could not be run, or answered something unreadable. None of that is worth failing
            // a session start over — but nor is it worth pretending the base is current, so it is reported instead.
            return _CouldNotCheck(branch);
        }
    }

    private static async Task<WorktreeSourceRefresh> _UpdateAsync(string root, string branch, TimeSpan mergeTimeout, CancellationToken cancellationToken)
    {
        // Refs are named in full for every revision range: a tag and a branch can share a name, and in a range the
        // tag wins — which would have this measuring the wrong history and calling a diverged branch up to date. The
        // short name is kept only for what the operator reads.
        var branchRef = $"refs/heads/{branch}";

        // The remote this branch pushes to, read from its own config rather than assumed to be "origin": a second
        // remote or a differently-named one is ordinary. No remote means nothing to be behind of — a local-only
        // repository forks from HEAD and that is not worth a word.
        var (couldReadConfig, remote) = await _TrackedRemoteAsync(root, branch, cancellationToken).ConfigureAwait(false);
        if (!couldReadConfig)
        {
            return _CouldNotCheck(branch);
        }

        if (remote is null)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.NoUpstream);
        }

        var fetched = await _FetchAsync(root, remote, cancellationToken).ConfigureAwait(false);
        var upstream = await _UpstreamOfAsync(root, branch, cancellationToken).ConfigureAwait(false);

        // Answered before the upstream is required to resolve, because a failed fetch is one reason it might not: a
        // remote-tracking ref that was never created leaves @{upstream} unresolvable, and reporting that as "this
        // branch tracks nothing" would silently swallow exactly the case this is here to make visible.
        if (!fetched)
        {
            var lastKnown = upstream is { } known
                ? await _CountCommitsAsync(root, $"{branchRef}..{known.Reference}", cancellationToken).ConfigureAwait(false)
                : null;

            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.FetchFailed,
                lastKnown ?? 0,
                upstream?.Display,
                $"Could not reach '{remote}', so this session forked from your local '{branch}' as it is"
                + (lastKnown > 0 ? $" — {_Commits(lastKnown.Value)} behind {upstream?.Display} at the last fetch." : "."));
        }

        if (upstream is not { } tracking)
        {
            // Configured to track something that is no longer there — the branch was merged and deleted on the
            // remote, and a prune took the ref with it. The config is intact; there is simply nothing left to be
            // behind of, so this stays as quiet as any other branch without an upstream. Saying "could not work it
            // out" here would put a warning on every session started from a finished feature branch.
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.NoUpstream);
        }

        var behind = await _CountCommitsAsync(root, $"{branchRef}..{tracking.Reference}", cancellationToken).ConfigureAwait(false);
        if (behind is null)
        {
            return _CouldNotCheck(branch);
        }

        if (behind == 0)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.UpToDate);
        }

        var ahead = await _CountCommitsAsync(root, $"{tracking.Reference}..{branchRef}", cancellationToken).ConfigureAwait(false);
        if (ahead is null)
        {
            return _CouldNotCheck(branch);
        }

        if (ahead > 0)
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.Diverged,
                behind.Value,
                tracking.Display,
                $"'{branch}' has {_Commits(ahead.Value)} of its own and is {_Commits(behind.Value)} behind {tracking.Display}, "
                + "so it was left untouched and this session forked from it as it is.");
        }

        // Not-knowing counts as holding changes here: leaving the operator's tree alone is the safe direction,
        // exactly as the worktree clean-gate treats an unreadable status.
        if (await _HasTrackedChangesAsync(root, cancellationToken).ConfigureAwait(false) is not false)
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.KeptLocalChanges,
                behind.Value,
                tracking.Display,
                $"'{branch}' is {_Commits(behind.Value)} behind {tracking.Display} but has uncommitted changes, so it was "
                + "left untouched and this session forked from it as it is.");
        }

        var inTheWay = await _UntrackedFilesInTheWayAsync(root, tracking.Reference, cancellationToken).ConfigureAwait(false);
        if (inTheWay is null)
        {
            return _CouldNotCheck(branch);
        }

        if (inTheWay.Count > 0)
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.UntrackedFilesInTheWay,
                behind.Value,
                tracking.Display,
                $"'{branch}' is {_Commits(behind.Value)} behind {tracking.Display}, but updating it would write over "
                + $"{_Name(inTheWay)} — files git is not keeping a copy of. It was left untouched and this session "
                + "forked from it as it is.");
        }

        var target = await _RevParseAsync(root, tracking.Reference, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return _CouldNotCheck(branch);
        }

        // Only ever --ff-only, and only in a tree with nothing of its own to lose: this is the operator's real
        // checkout, and a merge or a rebase in it would be a change they never asked for. Deliberately NOT given the
        // caller's token — a session start that is abandoned mid-checkout would otherwise leave half-written files
        // and a stale index.lock behind in that very tree.
        string? refusal = null;
        try
        {
            var fastForward = await GitCli.RunAsync(
                root,
                ["merge", "--ff-only", tracking.Reference],
                CancellationToken.None,
                GitEnvironment.NonInteractive,
                mergeTimeout).ConfigureAwait(false);

            refusal = fastForward.ExitCode == 0 ? null : GitCli.DescribeFailure(fastForward);
        }
        catch (InvalidOperationException)
        {
            refusal = $"it did not finish within {mergeTimeout.TotalSeconds:F0}s";
        }

        // Where the branch actually ended up, asked of the repository rather than inferred from how git exited. A
        // merge moves HEAD before it runs the post-merge hook, so a hook that outlives the timeout above is killed
        // with the update already done: trusting the exit code there would report failure and then fork the session
        // from the commit the branch has just left — the very staleness this exists to prevent.
        //
        // Uncancellable for the same reason the merge is. The tree has already been written to by the time this
        // runs, so a caller who gives up in this window would otherwise take the answer with them: the update would
        // stand, unreported, and the session start would fail on the cancellation instead. Finding out what happened
        // is part of doing it.
        var head = await _RevParseAsync(root, "HEAD", CancellationToken.None).ConfigureAwait(false);
        if (head is not null && head.Equals(target, StringComparison.Ordinal))
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.FastForwarded,
                behind.Value,
                tracking.Display,
                $"Updated '{branch}' to {tracking.Display} ({_Commits(behind.Value)}) before isolating this session.",
                head);
        }

        // A refusal leaves the tree untouched, but a guard that fires part-way through the checkout does not: git
        // writes the files before it moves the branch, so this is the one path that can hand back a working tree in
        // a state nobody asked for. Say that when it happens rather than letting it be discovered later.
        var disturbed = await _HasTrackedChangesAsync(root, CancellationToken.None).ConfigureAwait(false) is true;

        return new WorktreeSourceRefresh(
            WorktreeSourceOutcome.FastForwardFailed,
            behind.Value,
            tracking.Display,
            $"'{branch}' is {_Commits(behind.Value)} behind {tracking.Display} but could not be updated "
            + $"({refusal ?? "git reported success but the branch did not move"}), so this session forked from it as it is."
            + (disturbed ? $" Your checkout at {root} now has changes in it — worth a look before you carry on." : string.Empty));
    }

    private static WorktreeSourceRefresh _CouldNotCheck(string branch) => new(
        WorktreeSourceOutcome.CheckFailed,
        0,
        null,
        $"Could not work out whether '{branch}' was up to date, so this session forked from it as it is.");

    // Whether the fetch went through. Best-effort by design — offline, an expired credential or a remote that is
    // gone must never be the reason a session does not start. No --prune: dropping remote-tracking refs is a change
    // to the operator's repository nobody asked for, and losing the very ref the upstream resolves through would
    // turn this into a silent "tracks nothing" from then on.
    private static async Task<bool> _FetchAsync(string root, string remote, CancellationToken cancellationToken)
    {
        try
        {
            var fetch = await GitCli.RunAsync(
                root,
                ["fetch", remote],
                cancellationToken,
                GitEnvironment.NonInteractive,
                FetchTimeout).ConfigureAwait(false);

            return fetch.ExitCode == 0;
        }
        catch (InvalidOperationException)
        {
            // The timeout above, or a git that could not be run at all. Both mean the same thing here: we do not
            // know what the remote holds.
            return false;
        }
    }

    // The remote a branch pushes to. Read from branch.<name>.remote rather than parsed out of the upstream ref,
    // because a remote name can itself contain a slash and the split would guess wrong. A name that begins with "-"
    // is refused rather than passed to git, where it would read as an option.
    //
    // Three answers, not two: git says "no such key" with exit 1, which is an ordinary local-only branch and worth
    // no words, while any other exit is config it could not read — and calling that "tracks nothing" would turn a
    // broken config into permanent silence about a stale base.
    private static async Task<(bool CouldRead, string? Remote)> _TrackedRemoteAsync(string root, string branch, CancellationToken cancellationToken)
    {
        var configured = await GitCli.RunAsync(
            root,
            ["config", "--get", $"branch.{branch}.remote"],
            cancellationToken).ConfigureAwait(false);

        if (configured.ExitCode == 1)
        {
            return (true, null);
        }

        var remote = configured.StandardOutput.Trim();
        if (configured.ExitCode != 0)
        {
            return (false, null);
        }

        return (true, remote.Length > 0 && !remote.StartsWith('-') ? remote : null);
    }

    // git's own answer to "where does this branch push to", asked twice: the full ref for the revision ranges and the
    // merge, so nothing can shadow it, and the short form for the sentence the operator reads.
    private static async Task<(string Reference, string Display)?> _UpstreamOfAsync(string root, string branch, CancellationToken cancellationToken)
    {
        var full = await GitCli.RunAsync(
            root,
            ["rev-parse", "--symbolic-full-name", $"{branch}@{{upstream}}"],
            cancellationToken).ConfigureAwait(false);

        var reference = full.StandardOutput.Trim();
        if (full.ExitCode != 0 || reference.Length == 0)
        {
            return null;
        }

        var abbreviated = await GitCli.RunAsync(
            root,
            ["rev-parse", "--abbrev-ref", $"{branch}@{{upstream}}"],
            cancellationToken).ConfigureAwait(false);

        var display = abbreviated.StandardOutput.Trim();
        return (reference, abbreviated.ExitCode == 0 && display.Length > 0 ? display : reference);
    }

    // Null when git could not answer. "I could not measure" and "there is nothing to report" are the same number and
    // must not become the same answer: the whole feature is about never forking from an old base without saying so.
    private static async Task<int?> _CountCommitsAsync(string root, string range, CancellationToken cancellationToken)
    {
        var count = await GitCli.RunAsync(root, ["rev-list", "--count", range], cancellationToken).ConfigureAwait(false);
        return count.ExitCode == 0 && int.TryParse(count.StandardOutput.Trim(), out var commits) ? commits : null;
    }

    // Untracked files deliberately do not count as work in progress. Nearly every checkout carries some — build
    // output, a scratch file — and treating those as a reason not to update would mean the source never is, which is
    // the whole feature. What they must not do is stand where an incoming file lands, and that is asked separately
    // below rather than left to git: git refuses to overwrite an untracked file, but an *ignored* one it overwrites
    // without a word, and a local .env is exactly the file that gets ignored and exactly the one nobody has a second
    // copy of.
    // Null when git could not say. The two callers want opposite things from that: before the update, not knowing
    // means leave the tree alone; afterwards, not knowing is no reason to send the operator hunting through a
    // checkout that may be perfectly fine.
    private static async Task<bool?> _HasTrackedChangesAsync(string root, CancellationToken cancellationToken)
    {
        var status = await GitCli.RunAsync(
            root,
            ["status", "--porcelain", "--untracked-files=no"],
            cancellationToken).ConfigureAwait(false);

        return status.ExitCode == 0 ? status.StandardOutput.Trim().Length > 0 : null;
    }

    // The things the update would write that already exist here outside git's keeping — ignored ones included, which
    // is the point. Null when that could not be established, which is not the same as "none".
    private static async Task<IReadOnlyList<string>?> _UntrackedFilesInTheWayAsync(string root, string upstream, CancellationToken cancellationToken)
    {
        // -z on both, because git quotes and octal-escapes a non-ASCII path otherwise and the two lists would then
        // be compared in different alphabets.
        var incoming = await GitCli.RunAsync(root, ["diff", "--name-only", "-z", $"HEAD..{upstream}"], cancellationToken).ConfigureAwait(false);
        if (incoming.ExitCode != 0)
        {
            return null;
        }

        var arriving = incoming.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (arriving.Length == 0)
        {
            return [];
        }

        // Deliberately two decisions here. --others without --exclude-standard, because with the standard excludes
        // applied the ignored files — the only ones git would silently clobber — are exactly what drops out of the
        // answer. And no pathspec: a path that begins with ':' would be read as pathspec magic and quietly match
        // nothing, a symlinked directory is never descended into, and a long enough list overflows the command line
        // on Windows. Asking for everything untracked and doing the comparison here has none of those edges — and
        // it stays small, since this is one line per file, not per byte.
        var untracked = await GitCli.RunAsync(root, ["ls-files", "--others", "-z"], cancellationToken).ConfigureAwait(false);
        if (untracked.ExitCode != 0)
        {
            return null;
        }

        var comparison = await GitPaths.ComparisonForAsync(root, cancellationToken).ConfigureAwait(false);

        // Indexed rather than compared pair by pair: the untracked side deliberately includes ignored files, so in a
        // repository carrying a node_modules or an obj tree it runs to six figures, and multiplying that by the
        // incoming set would put seconds of pure string work on the session-start path.
        //
        // Two sets, and they must stay two. A path being written and a folder on the way to it collide with
        // different things: the file collides with something of the same name, the folder only with something
        // standing exactly where it has to go. Folding them together would make every untracked file that merely
        // shares a folder with an incoming one read as a collision — and a stray file in a touched folder is the
        // normal state of a working checkout, so the update would stop happening at all.
        var comparer = GitPaths.ComparerFor(comparison);
        var arrivingPaths = new HashSet<string>(arriving, comparer);
        var foldersOnTheWay = new HashSet<string>(arriving.SelectMany(GitPaths.ParentsOf), comparer);

        return untracked.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(existing =>
                arrivingPaths.Contains(existing)
                || foldersOnTheWay.Contains(existing)
                || GitPaths.ParentsOf(existing).Any(arrivingPaths.Contains))
            .ToList();
    }

    private static async Task<string?> _RevParseAsync(string root, string reference, CancellationToken cancellationToken)
    {
        var resolved = await GitCli.RunAsync(root, ["rev-parse", "--verify", $"{reference}^{{commit}}"], cancellationToken).ConfigureAwait(false);
        var commit = resolved.StandardOutput.Trim();

        return resolved.ExitCode == 0 && commit.Length > 0 ? commit : null;
    }

    private static string _Name(IReadOnlyList<string> paths) =>
        paths.Count > NamedCollisions
            ? $"{string.Join(", ", paths.Take(NamedCollisions))} and {paths.Count - NamedCollisions} more"
            : string.Join(", ", paths);

    private static string _Commits(int count) => count == 1 ? "1 commit" : $"{count} commits";
}
