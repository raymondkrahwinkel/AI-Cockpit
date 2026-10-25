namespace Cockpit.Plugins.Abstractions.TimeTracking;

// The shape of AC-1720's reading contract (#1448) until it reaches the epic; the sync deletes this file.
internal enum CockpitTimelineKind
{
    Status,
    Operator,
    Statusline,
    Closed,
    Stopped,
}

internal enum CockpitTimelineStatus
{
    Idle,
    Busy,
    WorkingBackground,
    Done,
    Failed,
    NeedsAttention,
}

internal enum CockpitOperatorAction
{
    MessageSent,
    PermissionAnswered,
    QuestionAnswered,
}

internal sealed record CockpitTimelineLine(
    CockpitTimelineKind Kind,
    DateTimeOffset At,
    string? PaneId,
    string? ProjectId,
    string? RunId,
    string? Ticket,
    CockpitTimelineStatus? Status,
    CockpitOperatorAction? Action,
    string? Statusline);

internal sealed record CockpitTimelineDay(
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

internal sealed record CockpitTimeline(
    IReadOnlyList<CockpitTimelineLine> Lines,
    IReadOnlyList<object> Usage,
    IReadOnlyList<CockpitTimelineDay> Days);
