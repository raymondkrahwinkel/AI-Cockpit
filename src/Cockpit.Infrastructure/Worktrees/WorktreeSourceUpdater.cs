using Cockpit.Core.Worktrees;

namespace Cockpit.Infrastructure.Worktrees;

// Brings the branch a worktree is about to fork from up to date first (AC-349), so a session does not start
// stale against the operator's last pull. The operator's tree is touched as little as possible — only a clean
// fast-forward — and every case this declines to handle forks from local HEAD instead and says so.
internal static class WorktreeSourceUpdater
{
    // Well short of GitCli's default hang guard, and only on the step that leaves the machine: a network that is
    // slow or gone should delay a session by seconds rather than minutes, not GitCli's full timeout.
    // Fallback is a fork from local HEAD, exactly as before this existed.
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    // The merge is local, but it runs the repository's post-merge hook, and a hook that installs packages can take
    // as long as it likes. Bounded so a session start cannot hang on someone else's hook.
    private static readonly TimeSpan MergeTimeout = TimeSpan.FromSeconds(60);

    // How many colliding paths to name before the message stops listing them.
    private const int NamedCollisions = 3;

    // `mergeTimeout`: Test seam: shorten the guard on the fast-forward so a killed merge can be driven deliberately.
    public static async Task<WorktreeSourceRefresh> BringUpToDateAsync(
        GitRepositoryInfo repository,
        WorktreeSourceHandling handling,
        CancellationToken cancellationToken,
        TimeSpan? mergeTimeout = null)
    {
        try
        {
            return repository.CurrentBranch is { } branch
                ? await _UpdateAsync(repository.Root, branch, handling, mergeTimeout ?? MergeTimeout, cancellationToken).ConfigureAwait(false)
                : await _UpdateDetachedAsync(repository.Root, repository.HeadCommit, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A git that timed out, could not be run, or answered something unreadable. None of that is worth failing
            // a session start over — but nor is it worth pretending the base is current, so it is reported instead.
            return repository.CurrentBranch is { } branch ? _CouldNotCheck(branch) : _CouldNotCheckDetached();
        }
    }

    // AC-1218: HEAD detached has no branch to fast-forward, but the commit it points at can still be stale — a
    // shared checkout left detached overnight silently became every spawned session's fork base. Only ever picks a
    // fresher commit to fork FROM; unlike the branch path above, nothing here ever writes to the checkout itself.
    private static async Task<WorktreeSourceRefresh> _UpdateDetachedAsync(string root, string headCommit, CancellationToken cancellationToken)
    {
        var remote = await _DefaultRemoteAsync(root, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.DetachedHead);
        }

        if (!await _FetchAsync(root, remote, cancellationToken).ConfigureAwait(false))
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.FetchFailed,
                0,
                null,
                $"Could not reach '{GitCli.RedactUrlCredentials(remote)}', so this session forked from the checkout's "
                + "detached HEAD as it is.");
        }

        var defaultBranch = await _DefaultBranchAsync(root, remote, cancellationToken).ConfigureAwait(false);
        if (defaultBranch is not { } resolved)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.DetachedHead);
        }

        var behind = await _CountCommitsAsync(root, $"{headCommit}..{resolved}", cancellationToken).ConfigureAwait(false);
        if (behind is null)
        {
            return _CouldNotCheckDetached();
        }

        if (behind == 0)
        {
            return WorktreeSourceRefresh.Quiet(WorktreeSourceOutcome.UpToDate);
        }

        var target = await _RevParseAsync(root, resolved, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return _CouldNotCheckDetached();
        }

        return new WorktreeSourceRefresh(
            WorktreeSourceOutcome.ForkedFromUpstream,
            behind.Value,
            resolved,
            $"The checkout's detached HEAD was {_Commits(behind.Value)} behind {resolved}, so this session forked "
            + $"from {resolved} instead — the checkout itself was left untouched.",
            target);
    }

    private static WorktreeSourceRefresh _CouldNotCheckDetached() => new(
        WorktreeSourceOutcome.CheckFailed,
        0,
        null,
        "Could not work out whether the checkout's detached HEAD was up to date, so this session forked from it as it is.");

    // The remote a detached checkout's default branch is expected to live on — "origin" when it exists, the same
    // remote every other convention in this file assumes, otherwise the sole remote if there is exactly one. More
    // than one non-origin remote is ambiguous, so nothing is picked rather than guessing which one matters.
    private static async Task<string?> _DefaultRemoteAsync(string root, CancellationToken cancellationToken)
    {
        var listing = await GitCli.RunAsync(root, ["remote"], cancellationToken).ConfigureAwait(false);
        if (listing.ExitCode != 0)
        {
            return null;
        }

        var remotes = listing.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(remote => remote.Trim())
            .Where(remote => remote.Length > 0)
            .ToList();

        if (remotes.Contains("origin", StringComparer.Ordinal))
        {
            return "origin";
        }

        return remotes.Count == 1 ? remotes[0] : null;
    }

    // The remote's own idea of its default branch, read fresh after the fetch above; "main" and "master" are
    // fallbacks for a repository whose origin/HEAD symbolic ref was never set locally (only `clone` creates it —
    // a plain `fetch` does not). Returns the remote-tracking ref name, or null when none of them resolve.
    private static async Task<string?> _DefaultBranchAsync(string root, string remote, CancellationToken cancellationToken)
    {
        var originHead = await GitCli.RunAsync(
            root, ["symbolic-ref", "--short", $"refs/remotes/{remote}/HEAD"], cancellationToken).ConfigureAwait(false);

        var candidates = new List<string>();
        var trimmed = originHead.StandardOutput.Trim();
        if (originHead.ExitCode == 0 && trimmed.StartsWith($"{remote}/", StringComparison.Ordinal))
        {
            candidates.Add(trimmed[(remote.Length + 1)..]);
        }

        candidates.Add("main");
        candidates.Add("master");

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            var reference = $"{remote}/{candidate}";
            if (await _RevParseAsync(root, reference, cancellationToken).ConfigureAwait(false) is not null)
            {
                return reference;
            }
        }

        return null;
    }

    private static async Task<WorktreeSourceRefresh> _UpdateAsync(
        string root,
        string branch,
        WorktreeSourceHandling handling,
        TimeSpan mergeTimeout,
        CancellationToken cancellationToken)
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

            // Redacted: this is usually a remote's name but git accepts a URL just as happily, which can carry a
            // token in its userinfo. This sentence reaches a toast and an agent's context, so this interpolation
            // is the one way the raw value could leak (git redacts its own stderr).
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.FetchFailed,
                lastKnown ?? 0,
                upstream?.Display,
                $"Could not reach '{GitCli.RedactUrlCredentials(remote)}', so this session forked from your local "
                + $"'{branch}' as it is"
                + (lastKnown > 0 ? $" — {_Commits(lastKnown.Value)} behind {upstream?.Display} at the last fetch." : "."));
        }

        if (upstream is not { } tracking)
        {
            // Configured to track something no longer there — merged and pruned on the remote. Config is intact,
            // simply nothing left to be behind of, so stays as quiet as a branch without an upstream (else every
            // finished feature branch would warn on session start).
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

        var target = await _RevParseAsync(root, tracking.Reference, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return _CouldNotCheck(branch);
        }

        // A creation that may not write to this checkout stops here and forks from the upstream tip instead —
        // same base a fast-forward would produce, without moving the operator's branch (AC-376). After the
        // diverged check since commits-only-here belong in the session base; before the rest since nothing writes here.
        if (handling == WorktreeSourceHandling.LeaveSourceAlone)
        {
            return new WorktreeSourceRefresh(
                WorktreeSourceOutcome.ForkedFromUpstream,
                behind.Value,
                tracking.Display,
                $"This session starts from {tracking.Display}; your own '{branch}' is {_Commits(behind.Value)} behind "
                + "it and was left where it is.",
                target);
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


        // Only ever --ff-only, and only in a tree with nothing to lose: this is the operator's real checkout, a
        // merge/rebase would be unasked-for. Deliberately NOT given the caller's token — an abandoned session start
        // must not leave half-written files and a stale index.lock behind.
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

        // Asked of the repository, not inferred from exit code: a merge moves HEAD before the post-merge hook
        // runs, so a hook killed by the timeout above would otherwise report failure and fork from the commit
        // the branch just left. Uncancellable for the same reason — the tree is already written by this point.
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

    // Whether the fetch went through. Best-effort by design — offline, an expired credential, or a gone remote
    // must never block session start. No --prune: dropping remote-tracking refs is an unasked-for change, and
    // losing the ref the upstream resolves through would turn this into silent "tracks nothing".
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

    // Read from branch.<name>.remote rather than parsed out of the upstream ref, since a remote name can contain
    // a slash. A name starting with "-" is refused rather than passed to git as an option. Three answers, not
    // two: exit 1 is "no such key" (ordinary local-only branch); any other non-zero exit is unreadable config.
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

    // Untracked files don't count as work in progress (nearly every checkout has some), but must not stand where
    // an incoming file lands — checked separately since git overwrites ignored files silently (e.g. a local .env).
    // Null when git could not say; the two callers read that differently: before update, not-knowing means leave alone.
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

        // --others without --exclude-standard, because standard excludes would drop the ignored files that git
        // would silently clobber. No pathspec: a leading ':' reads as pathspec magic, symlinked dirs aren't
        // descended into, and long lists overflow the command line on Windows — comparing here avoids all of that.
        var untracked = await GitCli.RunAsync(root, ["ls-files", "--others", "-z"], cancellationToken).ConfigureAwait(false);
        if (untracked.ExitCode != 0)
        {
            return null;
        }

        var comparison = await GitPaths.ComparisonForAsync(root, cancellationToken).ConfigureAwait(false);

        // Indexed rather than compared pair by pair: the untracked side includes ignored files and can run to six
        // figures (node_modules, obj), so pairwise comparison would add real seconds to session start. Two sets,
        // not one — folding a file-collision and a folder-on-the-way together would flag any stray file as a collision.
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
