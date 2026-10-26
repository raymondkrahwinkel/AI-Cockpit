namespace Cockpit.Plugins.Abstractions.TimeTracking;

public enum CockpitTimelineKind
{
    Status,
    Operator,
    Statusline,

    // The interval a session left open ends here: it went away, or the cockpit stopped with it open.
    Closed,

    // The cockpit stopped; no session.
    Stopped,
}

public enum CockpitTimelineStatus
{
    Idle,
    Busy,
    WorkingBackground,
    Done,
    Failed,
    NeedsAttention,
}

public enum CockpitOperatorAction
{
    MessageSent,
    PermissionAnswered,
    QuestionAnswered,
}

/// <summary>
/// Narrows <see cref="ICockpitTimelineReader.QueryAsync"/>; every field that is set must match. A line without a
/// session (<see cref="CockpitTimelineKind.Stopped"/>) passes only an empty filter.
/// </summary>
public sealed record CockpitTimelineFilter(string? ProjectId = null, string? PaneId = null, string? RunId = null);

/// <summary>
/// One line of the session timeline. Never a message or an answer; the statusline is the only text.
/// </summary>
public sealed record CockpitTimelineLine(
    CockpitTimelineKind Kind,
    DateTimeOffset At,
    string? PaneId,
    string? ProjectId,
    string? RunId,
    string? Ticket,
    CockpitTimelineStatus? Status,
    CockpitOperatorAction? Action,
    string? Statusline);

/// <summary>
/// What one turn added to its session: the difference from the session's record before it, not a running total.
/// Summing a session's deltas gives its last running total.
/// </summary>
public sealed record CockpitUsageDelta(
    string PaneId,
    DateTimeOffset RecordedAt,
    string? RunId,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    double CostUsd,
    int Turns);

/// <summary>
/// One session's local day older than the raw retention (90 days): its timeline and usage folded into one line.
/// A day here has no raw lines or deltas in the same answer, so nothing is counted twice.
/// </summary>
public sealed record CockpitTimelineDay(
    string PaneId,
    DateOnly Day,
    string? ProjectId,
    string? RunId,
    IReadOnlyList<string> Tickets,
    TimeSpan AgentTime,
    TimeSpan WaitingTime,
    int OperatorActions,
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    double ReportedCostUsd,
    int Turns,
    string? Model);

/// <summary>
/// The answer to one range: raw lines and per-turn deltas where they are still kept, day aggregates before that.
/// </summary>
public sealed record CockpitTimeline(
    IReadOnlyList<CockpitTimelineLine> Lines,
    IReadOnlyList<CockpitUsageDelta> Usage,
    IReadOnlyList<CockpitTimelineDay> Days);

/// <summary>
/// The timeline and usage of every session, not only the plugin's own (AC-1720). Needs
/// <c>"capabilities": ["timeline.read-all"]</c>; see <see cref="ICockpitHost.Timeline"/>.
/// </summary>
public interface ICockpitTimelineReader
{
    /// <summary>
    /// Lines and deltas with <c>from &lt;= At &lt; to</c>, and the day aggregates of the local days that overlap it.
    /// </summary>
    Task<CockpitTimeline> QueryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CockpitTimelineFilter? filter = null,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableCockpitTimelineReader : ICockpitTimelineReader
{
    public static ICockpitTimelineReader Instance { get; } = new UnavailableCockpitTimelineReader();

    public Task<CockpitTimeline> QueryAsync(DateTimeOffset from, DateTimeOffset to, CockpitTimelineFilter? filter = null, CancellationToken cancellationToken = default) =>
        Task.FromException<CockpitTimeline>(new NotSupportedException("This Cockpit host does not provide the session timeline."));
}
