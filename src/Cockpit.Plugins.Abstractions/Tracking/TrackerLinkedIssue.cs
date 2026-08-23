namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// Which side of a link an issue was read from (AC-346): <see cref="Outward"/> when the issue asked about is the
/// link's source, <see cref="Inward"/> when it is the target.
/// </summary>
public enum TrackerLinkDirection
{
    Outward,
    Inward,
}

/// <summary>
/// One issue linked to another (AC-346) — the general shape <see cref="ITrackerProvider.GetLinkedIssuesAsync"/>
/// hands back for every link an issue carries, whatever the tracker's own link-type vocabulary.
/// </summary>
/// <remarks>
/// <see cref="LinkType"/> is the tracker's own name for the link as read from the queried issue's side (e.g.
/// <c>"parent for"</c>, <c>"depends on"</c>). <see cref="Title"/>/<see cref="Stage"/> ride along so a caller
/// never has to round-trip for the linked issue's own snapshot; <see cref="Stage"/> is null when the tracker did
/// not report one.
/// </remarks>
public sealed record TrackerLinkedIssue(string LinkType, TrackerLinkDirection Direction, string IssueId, string Title, string? Stage);
