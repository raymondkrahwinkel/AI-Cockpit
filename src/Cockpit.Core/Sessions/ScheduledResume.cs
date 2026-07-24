namespace Cockpit.Core.Sessions;

/// <summary>
/// One prompt waiting to be sent to one session at one moment (AC-234) — what the cockpit does about an allowance
/// that has run out, and what an operator schedules by hand when they know they will not be at the desk.
/// <para>
/// Deliberately a single prompt with no follow-up: chaining, conditions and multi-step runs are Autopilot's job,
/// and it has its own approval flow. A resume that starts needing "and then" belongs there instead.
/// </para>
/// </summary>
/// <param name="PaneId">The session pane this was scheduled on, and where the prompt goes if that pane is still open.</param>
/// <param name="ConversationId">
/// The provider's own conversation id, so a session that has since been closed can be resumed by id rather than
/// started fresh. Null when the session never reported one; the resume then only has the open pane to aim at.
/// </param>
/// <param name="DueAt">When to send. For an allowance this is its reset moment; for a hand-scheduled resume, whatever the operator picked.</param>
/// <param name="Prompt">What to send — the provider's default ("continue") unless the operator wrote something else before scheduling.</param>
/// <param name="Reason">What this resume is waiting for, in the operator's words ("Week is 95% used"), so a pending line says why it exists.</param>
public sealed record ScheduledResume(
    string PaneId,
    string? ConversationId,
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
