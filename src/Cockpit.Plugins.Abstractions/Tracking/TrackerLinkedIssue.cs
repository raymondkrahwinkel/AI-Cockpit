namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// Which side of a link an issue was read from (AC-346): <see cref="Outward"/> when the issue asked about is the
/// link's source (e.g. an epic reading its "parent for" children), <see cref="Inward"/> when it is the target (e.g. a
/// sub reading its own "subtask of" parent). Carried alongside <see cref="TrackerLinkedIssue.LinkType"/> rather than
/// folded into it, so a consumer can filter on direction without parsing a tracker's own link-type prose.
/// </summary>
public enum TrackerLinkDirection
{
    Outward,
    Inward,
}

/// <summary>
/// One issue linked to another (AC-346) — the general shape <see cref="ITrackerProvider.GetLinkedIssuesAsync"/> hands
/// back for every link an issue carries, whatever the tracker's own link-type vocabulary. <see cref="LinkType"/> is the
/// tracker's own name for the link <em>as read from the queried issue's side</em> (YouTrack resolves this per
/// <see cref="Direction"/> already, so it reads naturally: an epic's children come back as <c>"parent for"</c>, a
/// sub's dependency as <c>"depends on"</c>) — a consumer (the epic-runner) filters on these tracker-agnostic strings
/// rather than the interface knowing what an epic or a dependency is. <see cref="Title"/>/<see cref="Stage"/> ride
/// along so a caller that only needs to decide from a link list never has to round-trip for the linked issue's own
/// snapshot; <see cref="Stage"/> is null when the tracker did not report one, same convention as
/// <see cref="TrackerIssueSnapshot.Stage"/>.
/// </summary>
public sealed record TrackerLinkedIssue(string LinkType, TrackerLinkDirection Direction, string IssueId, string Title, string? Stage);
