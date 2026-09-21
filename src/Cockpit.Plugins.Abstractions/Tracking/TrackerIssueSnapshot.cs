namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A read-only snapshot of an issue's title and stage (AC-411), as the tracker itself reports them right now — not as
/// a consumer's prose describes them. <see cref="Title"/> lets a gate (<see cref="ITrackerProvider.GetIssueSnapshotAsync"/>
/// callers such as Autopilot's child-stage check) test a marker like <c>[Brainstorm]</c> against the issue's own title
/// rather than a caller-supplied one it cannot verify; <see cref="Stage"/> is the tracker's own stage name(s), one per
/// line (a GitHub issue's labels can be several), or null when the tracker could not report it.
/// </summary>
/// <remarks>
/// <see cref="Description"/> and <see cref="Url"/> (AC-1339) let a caller that only has an issue id — Autopilot's
/// epic-runner picking a sub from a link list, which carries no description — read the same fields a direct click
/// already has. Both null when the tracker did not report them; a provider that does not implement this method at
/// all returns the interface's default snapshot, where they are null too.
/// </remarks>
public sealed record TrackerIssueSnapshot(string? Title, string? Stage, string? Description = null, string? Url = null);
