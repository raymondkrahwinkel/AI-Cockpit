namespace Cockpit.App.Services;

// How confidently a restored AI-session pane (AC-410) could pick up its earlier conversation — what
// `SessionRestorePlanner` answers before anything is minted. Every value still lets the pane come
// back and offer a fresh start; this only says whether offering to resume the old conversation is honest.
public enum SessionRestoreAvailability
{
    // Nothing is known yet: no saved `SessionStateRecord` for this pane, or one whose conversation id was never reported. Treated as "cannot resume" rather than a failure.
    Unknown,

    // The provider this session ran under keeps no resumable conversation at all (an HTTP model) — a fact reported by the session itself, not a lookup failure.
    Unsupported,

    // The profile this pane was started under no longer exists — renamed or deleted since (AC-410's known caveat: a profile is matched by label, so a rename reads as "gone").
    ProfileGone,

    // This pane ran in an isolated git worktree that is no longer on disk.
    WorktreeGone,

    // Everything needed to resume the earlier conversation is still in place.
    Known,

    // AC-1013: A resume was attempted and refused (pty exit in its degrade window, or SDK `error_during_execution`); unlike the values above, this one was tried and told no.
    Gone,
}
