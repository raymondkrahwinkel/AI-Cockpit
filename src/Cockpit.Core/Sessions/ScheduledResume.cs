namespace Cockpit.Core.Sessions;

// One prompt waiting to be sent to one session at one moment (AC-234) — what the cockpit does about an allowance
// that has run out, and what an operator schedules by hand when they know they will not be at the desk.
//
// Deliberately a single prompt with no follow-up: chaining, conditions and multi-step runs are Autopilot's job,
// and it has its own approval flow. A resume that starts needing "and then" belongs there instead.
//
// `PaneId`:
// The session pane this was scheduled on, and where the prompt goes. `ScheduledResumeCoordinator.RunDueAsync`
// sends straight into it only when it is already startable-into (`SessionPanelViewModel.CanTakeAPrompt`); a
// pane that is gone outright, or merely restored and not yet started, falls to
// `ScheduledResumeCoordinator.ReopenAndSend` instead (AC-290), which reopens the earlier conversation the same
// way the restore-offer banner's own "Resume conversation" does — but only for an SDK-kind pane this run's restart
// already brought back as an offer (AC-410's pane-id continuity + `SessionRestorePlanner`). A pane closed on
// purpose during this run, a live crash mid-session with no restart behind it, or a TTY pane (its `PromptSink`
// comes up only once the view's pty has actually started, too late to trust here) carries no reopen this can use,
// and the resume is reported as undelivered rather than sent nowhere.
// `DueAt`: When to send. For an allowance this is its reset moment; for a hand-scheduled resume, whatever the operator picked.
// `Prompt`: What to send — the provider's default ("continue") unless the operator wrote something else before scheduling.
// `Reason`: What this resume is waiting for, in the operator's words ("Week is 95% used"), so a pending line says why it exists.
public sealed record ScheduledResume(
    string PaneId,
    DateTimeOffset DueAt,
    string Prompt,
    string? Reason)
{
    // Whether this is due at `now` — its moment has arrived or already passed.
    public bool IsDue(DateTimeOffset now) => now >= DueAt;

    // Whether its moment passed while the cockpit was not running, by more than `grace`. Such a
    // resume is reported as lapsed rather than fired: something scheduled for 07:30 that arrives at 11:00 is a
    // surprise, not a service. The grace covers the ordinary case of the app being open and simply between ticks.
    public bool HasLapsed(DateTimeOffset now, TimeSpan grace) => now > DueAt + grace;
}
