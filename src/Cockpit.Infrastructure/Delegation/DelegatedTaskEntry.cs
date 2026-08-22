using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Delegation;

// The mutable state of one delegated task (#67) — the session it runs on plus what the orchestrator asks about.
// Kept internal to `DelegationService`; callers see the immutable `DelegatedTaskView`.
internal sealed class DelegatedTaskEntry
{
    public DelegatedTaskEntry(SessionProfile profile, DelegationRequest request)
    {
        Profile = profile;
        Prompt = request.Prompt;
        TaskType = request.TaskType;
        Label = request.Label;
        WorkingDirectory = request.WorkingDirectory;
        RequestedPermission = request.RequestedPermission;
        McpServers = request.McpServers;
    }

    public string TaskId { get; } = Guid.NewGuid().ToString("N");

    public SessionProfile Profile { get; }

    public string Prompt { get; }

    public string? TaskType { get; }

    public string? Label { get; }

    public string? WorkingDirectory { get; }

    // The caller's optional per-task permission request (AC-117). At or below the profile's ceiling it is honoured outright; above it, it is put to the operator as a one-time approval to run higher, and clamped to the ceiling if declined or nobody is there to ask. Null runs at the profile's own ceiling.
    public string? RequestedPermission { get; }

    // The caller's optional per-task MCP-server narrowing (AC-136), validated to a subset of what the profile allows before the task is accepted. Null runs with the profile's full allowed set.
    public IReadOnlyList<string>? McpServers { get; }

    // The verified pane that created this task (AC-128), or null off the verified path. Scopes the task-addressed tools and list_tasks so an agent cannot reach another session's task by naming its id (confused deputy).
    public string? OwnerPaneId { get; init; }

    // The project this task works on (AC-320) — inherited from the session that delegated it, because a sub-agent
    // asked to do a piece of its caller's work is working on the same thing the caller is. Null when that session
    // has no project, which runs exactly as delegation always has.
    //
    // Resolved once, when the task is accepted, and carried as a value: the start path hands it to the driver, where
    // looking it up again would mean the UI thread from a thread that is waiting on the answer (AC-218).
    public string? ProjectId { get; init; }

    // The ceiling this task's session was actually started under (AC-971) — read back to judge the changed-path
    // report, since files changed under a read-only ceiling are a boundary that was got past.
    public string? EffectiveCeiling { get; set; }

    // What git already called dirty when this task started (AC-971), so the report at the end names what this task
    // did rather than what the delegating session had lying around. Null when there was nothing to read.
    public IReadOnlySet<string>? WorkspaceBaseline { get; set; }

    // The host's own reading of what the task changed. Null until it has been taken, and null forever when it cannot be.
    public IReadOnlyList<string>? ChangedPaths { get; set; }

    public ISessionRuntime? Runtime { get; private set; }

    // Fires when the task outlives what its profile allows; cancelled the moment the task ends, so a finished task is never stopped after the fact.
    public CancellationTokenSource? TimeoutCancellation { get; set; }

    // Fires when a finished task's session has sat unused long enough to close; cancelled by a follow-up (which puts it back to work) or by a stop (which closes it anyway).
    public CancellationTokenSource? IdleCancellation { get; set; }

    public DelegatedTaskStatus Status { get; set; } = DelegatedTaskStatus.Queued;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public int TurnCount { get; set; }

    // Tool calls requested in the current turn — reset at each turn boundary, so the false-success guard judges each turn on its own (AC-100).
    public int ToolCallsRequested { get; set; }

    // Tool calls in the current turn that returned a non-error result (AC-100). Zero-while-requested is a no-op turn.
    public int ToolCallsSucceeded { get; set; }

    // Tool calls in the current turn that came back as an error — a denial by the delegated gate counts here (AC-100).
    public int ToolCallsErrored { get; set; }

    public string? Result { get; private set; }

    public string? Error { get; private set; }

    public bool IsFinished => Status is DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped;

    private int _worktreeReleaseClaimed;
    private int _startClaimed;

    // Claims the one worktree release this task gets (AC-106), so only the first caller performs it. The paths that
    // close a delegated session can overlap: an idle reap whose delay has already elapsed can no longer be cancelled
    // by a stop arriving at that instant, and both would then hand the same checkout back at once. A task is never
    // started again, so one release is all there is to hand out.
    public bool TryClaimWorktreeRelease() => Interlocked.Exchange(ref _worktreeReleaseClaimed, 1) == 0;

    // Claims the one start this task gets (AC-117). `Status` stays `DelegatedTaskStatus.Queued`
    // until after an operator-elevation consent wait resolves, so a task's own start can now await for a while
    // before it flips to `DelegatedTaskStatus.Running` — a window in which the queue drainer, seeing
    // the same still-Queued entry and a slot some other task just freed, could otherwise call
    // `DelegationService._StartAsync` on it a second time and spawn two sessions for one task.
    public bool TryClaimStart() => Interlocked.Exchange(ref _startClaimed, 1) == 0;

    public void Attach(ISessionRuntime runtime)
    {
        Runtime = runtime;
        _CancelIdle();
    }

    // Lets go of the session once it has been closed. The task keeps its result — what it produced outlives the session that produced it.
    public void ReleaseSession()
    {
        _CancelIdle();
        Runtime = null;
    }

    // Records the outcome. `keepSessionAlive` distinguishes a task that answered (its session
    // stays up so a follow-up turn is still possible) from one that was stopped or never started.
    //
    // Called more than once for a single failure, and deliberately so: a `SessionError` is not proof a session
    // is over (AC-106), so the turn that follows it still reports its own outcome and may correct the verdict. That
    // makes what a second call is allowed to overwrite the interesting question — see how `Error` is
    // kept below.
    public void Finish(DelegatedTaskStatus status, string? result, string? error, bool keepSessionAlive = false)
    {
        // The task is done, so its timeout must not fire later and stop a session that answered long ago.
        TimeoutCancellation?.Cancel();
        TimeoutCancellation?.Dispose();
        TimeoutCancellation = null;

        Status = status;
        Result = result ?? Result;

        // A later call carrying no reason must not erase one already recorded, the same way Result is kept above —
        // but only while the task is still failed. The two calls behind one failure are a SessionError that knows
        // why ("You've hit your usage limit…") followed by the turn's own completion, which reports failure with no
        // diagnostic of its own; plain assignment let the second wipe the first, and every failed delegation then
        // read as `error: null` to the operator and to get_task_result — undiagnosable, though the reason had been
        // in hand a millisecond earlier. Succeeding clears it instead: a follow-up turn reuses this same entry, and
        // a task that has since answered must not still carry the failure it recovered from.
        Error = status == DelegatedTaskStatus.Failed ? error ?? Error : error;
        FinishedAt = DateTimeOffset.Now;

        if (!keepSessionAlive)
        {
            _CancelIdle();
            Runtime = null;
        }
    }

    private void _CancelIdle()
    {
        IdleCancellation?.Cancel();
        IdleCancellation?.Dispose();
        IdleCancellation = null;
    }

    public DelegatedTaskView ToView() => new(
        TaskId,
        Profile.Label,
        Label,
        TaskType,
        Status,
        CreatedAt,
        StartedAt,
        FinishedAt,
        TurnCount,
        Result,
        Error,
        OwnerPaneId,
        EffectiveCeiling,
        ChangedPaths);
}
