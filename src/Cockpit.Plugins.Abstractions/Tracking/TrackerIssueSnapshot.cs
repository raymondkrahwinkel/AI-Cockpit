namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A read-only snapshot of an issue's title and stage (AC-411), as the tracker itself reports them right now — not as
/// a consumer's prose describes them. <see cref="Title"/> lets a gate (<see cref="ITrackerProvider.GetIssueSnapshotAsync"/>
/// callers such as Autopilot's child-stage check) test a marker like <c>[Brainstorm]</c> against the issue's own title
/// rather than a caller-supplied one it cannot verify; <see cref="Stage"/> is the tracker's own stage name(s), one per
/// line (a GitHub issue's labels can be several), or null when the tracker could not report it.
/// </summary>
public sealed record TrackerIssueSnapshot(string? Title, string? Stage);
