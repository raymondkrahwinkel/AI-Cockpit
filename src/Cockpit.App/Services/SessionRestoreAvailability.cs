namespace Cockpit.App.Services;

/// <summary>
/// How confidently a restored AI-session pane (AC-410) could pick up its earlier conversation — what
/// <see cref="SessionRestorePlanner"/> answers before anything is minted. Every value still lets the pane come
/// back and offer a fresh start; this only says whether offering to resume the old conversation is honest.
/// </summary>
public enum SessionRestoreAvailability
{
    /// <summary>Nothing is known yet: no saved <c>SessionStateRecord</c> for this pane, or one whose conversation id was never reported. Treated as "cannot resume" rather than a failure.</summary>
    Unknown,

    /// <summary>The provider this session ran under keeps no resumable conversation at all (an HTTP model) — a fact reported by the session itself, not a lookup failure.</summary>
    Unsupported,

    /// <summary>The profile this pane was started under no longer exists — renamed or deleted since (AC-410's known caveat: a profile is matched by label, so a rename reads as "gone").</summary>
    ProfileGone,

    /// <summary>This pane ran in an isolated git worktree that is no longer on disk.</summary>
    WorktreeGone,

    /// <summary>Everything needed to resume the earlier conversation is still in place.</summary>
    Known,

    /// <summary>
    /// A resume was actually attempted and the provider refused it — the pty exited within its degrade window
    /// (<c>TtyViewModel._degradeInsteadOfCloseOnExit</c>), or an SDK turn came back <c>error_during_execution</c>
    /// with no result. Distinct from every value above: those describe a conversation nobody tried to reach yet,
    /// this one was tried and told no. The pane's own launch output or the provider's <c>errors[]</c> is the
    /// explanation, not a guess.
    /// </summary>
    Gone,
}
