using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Sessions;

// AC-1437: what a session's events fold to besides its rows, as its host keeps it. Replaced whole on every change, and
// its lists are only swapped for new ones, so equality says whether anything changed.
public sealed record SessionLiveState(
    SessionStatus Status,
    IReadOnlyList<SessionActiveToolCall> ActiveToolCalls,
    IReadOnlyList<BackgroundTask> BackgroundTasks,
    SessionUsageTotals Usage,
    SessionConnection? Connection,
    string? CliSessionId,
    string? Model = null,
    string? PermissionMode = null)
{
    public static SessionLiveState None { get; } = new(SessionStatus.Idle, [], [], SessionUsageTotals.None, Connection: null, CliSessionId: null);
}

// AC-532: one top-level tool call the turn is waiting on.
public sealed record SessionActiveToolCall(string ToolUseId, string ToolName, DateTimeOffset StartedAt);

// What the session reported when it came up: the directory it runs in, its tools, and the model it resolved to.
public sealed record SessionConnection(string WorkingDirectory, IReadOnlyList<string> Tools, string? Model);

// A turn's `usage` covers only that turn, so it sums; `total_cost_usd` is the session's cost so far, so it replaces
// the previous figure (AC-564).
public sealed record SessionUsageTotals(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens,
    double TotalCostUsd,
    int Turns)
{
    public static SessionUsageTotals None { get; } = new(0, 0, 0, 0, 0, 0);

    public int TotalTokens => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    // A pure-error session with no usage has nothing worth showing.
    public bool HasData => TotalTokens > 0 || TotalCostUsd > 0;

    // A turn without usage (an error result) adds nothing but still counts as a turn.
    public SessionUsageTotals Add(TokenUsage? usage, double? costUsd) => new(
        InputTokens + (usage?.InputTokens ?? 0),
        OutputTokens + (usage?.OutputTokens ?? 0),
        CacheReadInputTokens + (usage?.CacheReadInputTokens ?? 0),
        CacheCreationInputTokens + (usage?.CacheCreationInputTokens ?? 0),
        costUsd ?? TotalCostUsd,
        Turns + 1);
}

// How a turn ended: completed (an error result included) or broken off by a session error, which drains no queue.
// `FailedRowId` is the failure row the end formed, if any; `FailureReason` is the provider's own words for it.
public sealed record SessionTurnEnd(
    bool IsError,
    bool BySessionError,
    bool WasInterrupted,
    string? FailedRowId,
    string? Subtype,
    string? FailureReason);

// AC-1057: the provider's own verdict on one background task.
public sealed record SessionBackgroundTaskNotice(string TaskId, string? ToolUseId, BackgroundTaskStatus Status);
