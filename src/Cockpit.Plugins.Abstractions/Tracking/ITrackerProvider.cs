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

    /// <summary>
    /// The names of the MCP servers that host this tracker's READ-only tools — reading an issue, following its
    /// linked / "parent for" child issues, searching (AC-212/AC-217). A consumer (Autopilot) scopes these into its
    /// planning session so the CEO can inspect the source issue and, for an epic, pull its children before it drafts the
    /// plan — while the tracker's WRITE tools (stage moves, notes) stay out of planning and remain the run's alone.
    /// Names as the host advertises them via <see cref="ICockpitHost.AddMcpServer"/>. Default empty: a tracker whose
    /// reads go through a CLI rather than an MCP server (GitHub Issues via <c>gh</c>) contributes none, and the consumer
    /// falls back to that path. Providers list only the servers that actually exist (a configured, opted-in instance).
    /// </summary>
    IReadOnlyList<string> ReadToolMcpServerNames => [];

    /// <summary>Posts a comment on the issue. Returns whether it landed.</summary>
    Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default);

    /// <summary>Moves the issue to <paramref name="stage"/> — a stage name in the tracker's own vocabulary. Returns whether it landed (false when the tracker has no such stage, or none at all).</summary>
    Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tracker's own stage name for a tracker-neutral <see cref="TrackerWorkStage"/> in a consumer's work lifecycle
    /// (AC-202), or null when this tracker has no stage to map it to — the consumer then leaves the issue where it is.
    /// Lets Autopilot move a source issue automatically at run-start and merge-ready without knowing any tracker's
    /// vocabulary. The default returns null (no auto-mapping), so a provider that does not opt in keeps compiling and is
    /// simply never auto-moved; a provider maps only the stages it has a column for.
    /// </summary>
    string? SuggestStageName(TrackerWorkStage stage) => null;

    /// <summary>Attaches a file to the issue (a verify screenshot). Returns whether it landed — false when the tracker has no attachment channel (GitHub Issues), so a consumer can fall back to a comment.</summary>
    Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default);

    /// <summary>Reads the issue's comments (AC-155), oldest to newest — what a consumer polls to see the operator's reply to a blockade question. An empty list on failure, never a throw.</summary>
    Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads this issue's own title and stage(s) right now, in the tracker's own vocabulary — a stage is one per line
    /// (a YouTrack issue carries a single one; a GitHub issue's labels are its stage-equivalent, so it can carry
    /// several) — or a snapshot with both null when it could not be read. Lets a consumer (Autopilot, AC-411) check an
    /// item it did not receive through a "start" intent — an epic's child, pulled in by the CEO during planning
    /// rather than clicked by the operator — against the same executable-stage gate the clicked item already passed,
    /// using the tracker's own title rather than trusting a caller-supplied one for a <c>[Brainstorm]</c> check.
    /// Default returns an empty snapshot: a provider that does not opt in keeps compiling, and a consumer treats
    /// "cannot tell" as not ready rather than silently skipping the check.
    /// </summary>
    Task<TrackerIssueSnapshot> GetIssueSnapshotAsync(string issueId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrackerIssueSnapshot(null, null));

    /// <summary>
    /// Every issue linked to <paramref name="issueId"/> — an epic's children, a sub's dependencies, and anything
    /// else the tracker carries as a link — with each link's own type, direction and the linked issue's id/title/stage
    /// (AC-346). Deliberately general rather than two epic/dependency-shaped methods: a consumer (Autopilot's
    /// epic-runner) filters the tracker-neutral <see cref="TrackerLinkedIssue.LinkType"/> strings itself (e.g.
    /// <c>"parent for"</c>, <c>"depends on"</c>), so this interface never has to know what an epic is. Default returns
    /// an empty list and never throws: a provider that does not opt in keeps compiling, and a consumer treats "no links
    /// reported" the same as "genuinely has none" — an epic-runner degrades to "not an epic" rather than failing, which
    /// is the correct fallback for a tracker whose plugin has not shipped this yet.
    /// <para>
    /// Deliberate exception to this interface's own fail-soft convention (AC-346 review): a provider that performs
    /// real I/O for this method (YouTrack's does) is allowed to let a genuine read failure propagate as an exception
    /// instead of degrading to an empty list, because the two outcomes mean very different things to a caller deciding
    /// whether an issue is an epic — "no links" (proceed as an ordinary issue) versus "could not read links right now"
    /// (do not guess; pause and retry). Every other method here collapses that distinction because collapsing it is
    /// harmless there (a comment that did not land is simply not posted); here it is not, since guessing wrong could
    /// silently plan the wrong item or skip a merge check. A consumer that cannot afford the distinction may still
    /// treat any outcome — return or throw — as "no links" by catching around the call.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TrackerLinkedIssue>> GetLinkedIssuesAsync(string issueId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrackerLinkedIssue>>([]);
}
