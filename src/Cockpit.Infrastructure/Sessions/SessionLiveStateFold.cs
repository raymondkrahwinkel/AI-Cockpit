using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1437: what a session's events fold to besides its rows, which `SessionViewModel.Apply` used to keep itself.
// One thread only — the consumer's, like `SessionTranscriptBuilder` — so nothing here locks.
internal sealed class SessionLiveStateFold
{
    private IReadOnlyList<SessionActiveToolCall> _activeToolCalls = [];
    private IReadOnlyList<BackgroundTask> _backgroundTasks = [];
    private SessionUsageTotals _usage = SessionUsageTotals.None;
    private SessionConnection? _connection;
    private string? _cliSessionId;

    // A pending permission or a CLI `needs_action`, cleared when the operator sends the next message or answers the
    // last prompt; stickier than the prompt itself, so the sidebar keeps flagging until someone has been back.
    private bool _needsAttention;

    // So an idle session reads as Done rather than Idle once any turn finished (T4).
    private bool _hasCompletedATurn;

    // From a session error until a fresh send (busy outranks it) or the next completed turn supersedes it (AC-1309).
    private bool _lastTurnFailed;

    // Folds one event, after the transcript has formed its rows; returns how a turn ended and what the provider said
    // about a background task, when this event did either.
    public (SessionTurnEnd? End, SessionBackgroundTaskNotice? Notice) Apply(
        SessionEvent evt,
        TranscriptFold fold,
        bool interruptRequested,
        DateTimeOffset now)
    {
        // AC-1088: a resumed session carries its id from its first event, and a `/clear` gives it a new one mid-session.
        _cliSessionId = evt.SessionId ?? _cliSessionId;

        switch (evt)
        {
            case SessionInitialized init:
                _connection = new SessionConnection(init.Cwd, init.Tools, init.Model);
                break;

            // AC-146: a sub-agent's own tool call nests under its Task row and is not what the turn waits on.
            case ToolUseRequested toolUse when !fold.InSubAgentLane && fold.Row is not null:
                _activeToolCalls = [.. _activeToolCalls, new SessionActiveToolCall(toolUse.ToolUseId, toolUse.ToolName, now)];
                break;

            // AC-532: no longer outstanding, whichever way it resolved, a permission denial included.
            case ToolResult result when !fold.InSubAgentLane && _activeToolCalls.Any(call => call.ToolUseId == result.ToolUseId):
                _activeToolCalls = [.. _activeToolCalls.Where(call => call.ToolUseId != result.ToolUseId)];
                break;

            // AC-215: a pre-authorized tool of a self-driving run was allowed without asking, so nobody waits on it.
            case PermissionRequested when fold.Row is { IsPendingPermission: true }:
                _needsAttention = true;
                break;

            case SessionStatusChanged { NeedsAction.Length: > 0 }:
                _needsAttention = true;
                break;

            // The complete set every time, so a dropped event costs one stale reading, not a desynchronised ledger.
            case BackgroundTasksChanged backgroundTasks:
                _backgroundTasks = backgroundTasks.Tasks;
                break;

            case BackgroundTaskNotification notification:
                return (null, new SessionBackgroundTaskNotice(notification.TaskId, notification.ToolUseId, notification.Status));

            // AC-532: every turn ends here or in a session error, whether or not each tool call got its result.
            // AC-531: the background tasks deliberately outlive the turn.
            case TurnCompleted turn:
                _ClearActiveToolCalls();
                _hasCompletedATurn = true;
                _lastTurnFailed = false;
                _usage = _usage.Add(turn.Usage, turn.TotalCostUsd);
                return (new SessionTurnEnd(
                    turn.IsError,
                    BySessionError: false,
                    WasInterrupted: interruptRequested,
                    FailedRowId: fold.Row is { IsFailedTurnRow: true } failedRow ? failedRow.Id : null,
                    turn.Subtype,
                    FailureReason: turn.Errors is { Count: > 0 } errors ? string.Join('\n', errors) : null), null);

            // Whatever was outstanding died with the session (AC-276): no ToolResult or task update will clear it now.
            case SessionError error:
                _ClearActiveToolCalls();
                _backgroundTasks = [];
                _lastTurnFailed = true;
                return (new SessionTurnEnd(
                    IsError: true,
                    BySessionError: true,
                    WasInterrupted: false,
                    FailedRowId: fold.Row?.Id,
                    Subtype: null,
                    FailureReason: error.Message), null);
        }

        return (null, null);
    }

    public SessionLiveState Snapshot(bool isBusy) => new(
        (_needsAttention, isBusy, _backgroundTasks.Any(task => task.Kind == BackgroundTaskKind.SubAgent), _lastTurnFailed) switch
        {
            (true, _, _, _) => SessionStatus.NeedsAttention,
            (false, true, _, _) => SessionStatus.Busy,
            (false, false, true, _) => SessionStatus.WorkingBackground,
            (false, false, false, true) => SessionStatus.Failed,
            (false, false, false, false) => _hasCompletedATurn ? SessionStatus.Done : SessionStatus.Idle,
        },
        _activeToolCalls,
        _backgroundTasks,
        _usage,
        _connection,
        _cliSessionId);

    public void ClearNeedsAttention() => _needsAttention = false;

    // A conversation that starts over in the same pane (AC-564): the turn's state and the numbers go with it.
    public void Reset()
    {
        _ClearActiveToolCalls();
        _needsAttention = false;
        _hasCompletedATurn = false;
        _lastTurnFailed = false;
        _usage = SessionUsageTotals.None;
    }

    private void _ClearActiveToolCalls()
    {
        if (_activeToolCalls.Count > 0)
        {
            _activeToolCalls = [];
        }
    }
}
