namespace Cockpit.Core.Worktrees;

// What happened to the source branch when a worktree was forked from it (AC-349). A session must start on the
// latest state of the branch it isolates, so the create path fetches first and fast-forwards the source when it
// safely can; these are the states that attempt can end in.
public enum WorktreeSourceOutcome
{
    // The source was already on its upstream tip — nothing to bring across, nothing to say.
    UpToDate,

    // The source was behind and clean, so it was fast-forwarded and the worktree forked from the new tip.
    FastForwarded,

    // The source was behind and could have been updated, but this creation may not write to it — so the worktree forked from the upstream tip and the branch stayed where it was (AC-376).
    ForkedFromUpstream,

    // The source was behind but its working tree held changes, so it was left alone and the fork came from the local HEAD.
    KeptLocalChanges,

    // The source held commits of its own that the upstream does not, so it was left alone — a fast-forward would not have been one.
    Diverged,

    // The update would have written over a file git is not tracking — an ignored `.env` and its kind, which git overwrites without a word — so the source was left alone.
    UntrackedFilesInTheWay,

    // The source was behind and clean, but git refused the fast-forward anyway (a lock, a hook that said no).
    FastForwardFailed,

    // Whether the source was current could not be established at all — git errored or timed out — so the fork base is the local HEAD and that is said out loud rather than assumed to be fine.
    CheckFailed,

    // The remote could not be reached, so how far the source lags is only as fresh as the last fetch.
    FetchFailed,

    // The branch tracks nothing, so there is no upstream to be behind — a local-only repository, or a branch never pushed.
    NoUpstream,

    // HEAD was detached and either origin had nothing newer or it could not be compared against at all (no
    // remote, or no resolvable default branch) — so the commit HEAD already pointed at is the fork commit.
    DetachedHead,
}
