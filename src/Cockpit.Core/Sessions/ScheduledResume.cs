namespace Cockpit.Core.Sessions;

/// <summary>
/// One prompt waiting to be sent to one session at one moment (AC-234) — what the cockpit does about an allowance
/// that has run out, and what an operator schedules by hand when they know they will not be at the desk.
/// <para>
/// Deliberately a single prompt with no follow-up: chaining, conditions and multi-step runs are Autopilot's job,
/// and it has its own approval flow. A resume that starts needing "and then" belongs there instead.
/// </para>
/// </summary>
/// <param name="PaneId">
/// The session pane this was scheduled on, and where the prompt goes. <c>ScheduledResumeCoordinator.RunDueAsync</c>
/// sends straight into it only when it is already startable-into (<c>SessionPanelViewModel.CanTakeAPrompt</c>); a
/// pane that is gone outright, or merely restored and not yet started, falls to
/// <c>ScheduledResumeCoordinator.ReopenAndSend</c> instead (AC-290), which reopens the earlier conversation the same
/// way the restore-offer banner's own "Resume conversation" does — but only for an SDK-kind pane this run's restart
/// already brought back as an offer (AC-410's pane-id continuity + <c>SessionRestorePlanner</c>). A pane closed on
/// purpose during this run, a live crash mid-session with no restart behind it, or a TTY pane (its <c>PromptSink</c>
/// comes up only once the view's pty has actually started, too late to trust here) carries no reopen this can use,
/// and the resume is reported as undelivered rather than sent nowhere.
/// </param>
/// <param name="DueAt">When to send. For an allowance this is its reset moment; for a hand-scheduled resume, whatever the operator picked.</param>
/// <param name="Prompt">What to send — the provider's default ("continue") unless the operator wrote something else before scheduling.</param>
/// <param name="Reason">What this resume is waiting for, in the operator's words ("Week is 95% used"), so a pending line says why it exists.</param>
public sealed record ScheduledResume(
    string PaneId,
    DateTimeOffset DueAt,
    string Prompt,
    string? Reason)
{
    /// <summary>Whether this is due at <paramref name="now"/> — its moment has arrived or already passed.</summary>
    public bool IsDue(DateTimeOffset now) => now >= DueAt;

    /// <summary>
    /// Whether its moment passed while the cockpit was not running, by more than <paramref name="grace"/>. Such a
    /// resume is reported as lapsed rather than fired: something scheduled for 07:30 that arrives at 11:00 is a
    /// surprise, not a service. The grace covers the ordinary case of the app being open and simply between ticks.
    /// </summary>
    public bool HasLapsed(DateTimeOffset now, TimeSpan grace) => now > DueAt + grace;
}
