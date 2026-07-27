namespace Cockpit.Core.Worktrees;

/// <summary>
/// What happened to the source branch when a worktree was forked from it (AC-349). A session must start on the
/// latest state of the branch it isolates, so the create path fetches first and fast-forwards the source when it
/// safely can; these are the states that attempt can end in.
/// </summary>
public enum WorktreeSourceOutcome
{
    /// <summary>The source was already on its upstream tip — nothing to bring across, nothing to say.</summary>
    UpToDate,

    /// <summary>The source was behind and clean, so it was fast-forwarded and the worktree forked from the new tip.</summary>
    FastForwarded,

    /// <summary>The source was behind but its working tree held changes, so it was left alone and the fork came from the local HEAD.</summary>
    KeptLocalChanges,

    /// <summary>The source held commits of its own that the upstream does not, so it was left alone — a fast-forward would not have been one.</summary>
    Diverged,

    /// <summary>The update would have written over a file git is not tracking — an ignored <c>.env</c> and its kind, which git overwrites without a word — so the source was left alone.</summary>
    UntrackedFilesInTheWay,

    /// <summary>The source was behind and clean, but git refused the fast-forward anyway (a lock, a hook that said no).</summary>
    FastForwardFailed,

    /// <summary>Whether the source was current could not be established at all — git errored or timed out — so the fork base is the local HEAD and that is said out loud rather than assumed to be fine.</summary>
    CheckFailed,

    /// <summary>The remote could not be reached, so how far the source lags is only as fresh as the last fetch.</summary>
    FetchFailed,

    /// <summary>The branch tracks nothing, so there is no upstream to be behind — a local-only repository, or a branch never pushed.</summary>
    NoUpstream,

    /// <summary>HEAD was detached, so there is no source branch to update; the fork commit is the one HEAD points at.</summary>
    DetachedHead,
}
