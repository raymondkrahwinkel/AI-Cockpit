namespace Cockpit.Plugin.YouTrack;

// A ticket's status was moved from the cockpit — the second act a flow can start on (#69), next to picking a ticket
// for a session. One instance is shared by the contributions that can move a ticket (the issues dialog and a
// session's header), so a flow hears about the move wherever it was made from.
//
// It carries what a status change actually is: which ticket, where it came from, and where it went. "The ticket
// moved" says little; "WEB-14 went from In Progress to Review" is what a flow can decide on — a rule that comments
// on a pull request when a ticket reaches Review has to know it is Review, and that it was not already.
//
// Deliberately fired only for moves the operator makes here, not for the ones the "Set ticket status" workflow step
// makes: a flow that moves a ticket would otherwise trigger itself, and a status change that a flow performed is
// something that flow already knows about.
internal sealed class IssueStateChanges
{
    // Raised (on the caller's thread — every move here is made on the UI thread) after YouTrack accepted the move.
    public event EventHandler<IssueStateChanged>? Changed;

    public void Moved(YouTrackInstance instance, YouTrackIssue issue, string previousState, string newState, string? workingDirectory = null) =>
        Changed?.Invoke(this, new IssueStateChanged(instance, issue, previousState, newState, workingDirectory));
}

// A ticket moved: which one, on which instance, from what to what, and where the session that moved it works.
internal sealed record IssueStateChanged(
    YouTrackInstance Instance,
    YouTrackIssue Issue,
    string PreviousState,
    string NewState,
    string? WorkingDirectory);
