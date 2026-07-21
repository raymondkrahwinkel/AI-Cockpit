namespace Cockpit.Plugins.Abstractions.Tracking;

/// <summary>
/// A tracker a plugin can post back to (AC-154): the writing half of an issue tracker, tracker-neutral so a consumer
/// (Autopilot) can leave evidence and move an issue's stage without knowing whether it is YouTrack, GitHub Issues, or
/// another. A tracker plugin registers one with <see cref="ICockpitHost.AddTrackerProvider"/>; a consumer picks it by
/// <see cref="TrackerId"/> (the same id the "start" intent carries — <c>youtrack</c>, <c>github-issues</c>). Every
/// method returns whether it landed rather than throwing, so a tracker action that fails does not take a run down; the
/// stage names are the tracker's own, mapped by the consumer's configuration.
/// </summary>
public interface ITrackerProvider
{
    /// <summary>The tracker's id — matches the <c>tracker</c> a "start" intent carries, e.g. <c>youtrack</c> or <c>github-issues</c>.</summary>
    string TrackerId { get; }

    /// <summary>Posts a comment on the issue. Returns whether it landed.</summary>
    Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default);

    /// <summary>Moves the issue to <paramref name="stage"/> — a stage name in the tracker's own vocabulary. Returns whether it landed (false when the tracker has no such stage, or none at all).</summary>
    Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default);

    /// <summary>Attaches a file to the issue (a verify screenshot). Returns whether it landed — false when the tracker has no attachment channel (GitHub Issues), so a consumer can fall back to a comment.</summary>
    Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default);

    /// <summary>Reads the issue's comments (AC-155), oldest to newest — what a consumer polls to see the operator's reply to a blockade question. An empty list on failure, never a throw.</summary>
    Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default);
}
