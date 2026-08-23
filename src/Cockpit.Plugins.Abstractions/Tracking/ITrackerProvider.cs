namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A tracker a plugin can post back to (AC-154): the writing half of an issue tracker, tracker-neutral so a
/// consumer can leave evidence and move an issue's stage without knowing which tracker it is.
/// </summary>
/// <remarks>
/// A tracker plugin registers one with <see cref="ICockpitHost.AddTrackerProvider"/>; a consumer picks it by
/// <see cref="TrackerId"/>. Every method returns whether it landed rather than throwing.
/// </remarks>
public interface ITrackerProvider
{
    /// <summary>
    /// The tracker's id — matches the <c>tracker</c> a "start" intent carries, e.g. <c>youtrack</c> or <c>github-issues</c>.
    /// </summary>
    string TrackerId { get; }

    /// <summary>
    /// The names of the MCP servers that host this tracker's READ-only tools — reading an issue, following its
    /// links, searching (AC-212/AC-217). A consumer scopes these into a planning session while the tracker's
    /// WRITE tools stay out.
    /// </summary>
    /// <remarks>
    /// Names as the host advertises them via <see cref="ICockpitHost.AddMcpServer"/>. Default empty: a tracker
    /// whose reads go through a CLI rather than an MCP server contributes none.
    /// </remarks>
    IReadOnlyList<string> ReadToolMcpServerNames => [];

    /// <summary>
    /// Posts a comment on the issue. Returns whether it landed.
    /// </summary>
    Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the issue to <paramref name="stage"/> — a stage name in the tracker's own vocabulary. Returns whether it landed (false when the tracker has no such stage, or none at all).
    /// </summary>
    Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tracker's own stage name for a tracker-neutral <see cref="TrackerWorkStage"/> in a consumer's work
    /// lifecycle (AC-202), or null when this tracker has no stage to map it to.
    /// </summary>
    /// <remarks>
    /// Default returns null (no auto-mapping); a provider maps only the stages it has a column for.
    /// </remarks>
    string? SuggestStageName(TrackerWorkStage stage) => null;

    /// <summary>
    /// Attaches a file to the issue (a verify screenshot). Returns whether it landed — false when the tracker has no attachment channel (GitHub Issues), so a consumer can fall back to a comment.
    /// </summary>
    Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the issue's comments (AC-155), oldest to newest — what a consumer polls to see the operator's reply to a blockade question. An empty list on failure, never a throw.
    /// </summary>
    Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads this issue's own title and stage(s) right now, in the tracker's own vocabulary, or a snapshot with
    /// both null when it could not be read.
    /// </summary>
    /// <remarks>
    /// Default returns an empty snapshot: a provider that does not opt in keeps compiling, and a consumer treats
    /// "cannot tell" as not ready rather than silently skipping the check.
    /// </remarks>
    Task<TrackerIssueSnapshot> GetIssueSnapshotAsync(string issueId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrackerIssueSnapshot(null, null));

    /// <summary>
    /// Every issue linked to <paramref name="issueId"/> — an epic's children, a sub's dependencies, anything else
    /// the tracker carries as a link — with each link's own type, direction and the linked issue's id/title/stage
    /// (AC-346).
    /// </summary>
    /// <remarks>
    /// Default returns an empty list and never throws. Deliberate exception to that fail-soft convention: a
    /// provider that performs real I/O for this method (YouTrack's does) may let a genuine read failure propagate
    /// as an exception instead, since "no links" and "could not read links" mean different things to a caller
    /// deciding whether an issue is an epic.
    /// </remarks>
    Task<IReadOnlyList<TrackerLinkedIssue>> GetLinkedIssuesAsync(string issueId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrackerLinkedIssue>>([]);
}
