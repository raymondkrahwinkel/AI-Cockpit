namespace Cockpit.Core.Sessions;

// AC-234: one prompt waiting to be sent to one session at one moment, deliberately with no follow-up —
// chaining and conditions are Autopilot's job. `PaneId` reopens via `ScheduledResumeCoordinator.ReopenAndSend`
// (AC-290) when not directly startable-into; `DueAt`/`Prompt`/`Reason` are the trigger time, message, and why.
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
