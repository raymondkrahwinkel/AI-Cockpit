using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// The mutable state of one delegated task (#67) — the session it runs on plus what the orchestrator asks about.
/// Kept internal to <see cref="DelegationService"/>; callers see the immutable <see cref="DelegatedTaskView"/>.
/// </summary>
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

    /// <summary>The caller's optional per-task least-privilege cap (AC-117), clamped to the profile ceiling when the session starts. Null runs at the profile's own ceiling.</summary>
    public string? RequestedPermission { get; }

    /// <summary>The caller's optional per-task MCP-server narrowing (AC-136), validated to a subset of what the profile allows before the task is accepted. Null runs with the profile's full allowed set.</summary>
    public IReadOnlyList<string>? McpServers { get; }

    /// <summary>The verified pane that created this task (AC-128), or null off the verified path. Scopes the task-addressed tools and list_tasks so an agent cannot reach another session's task by naming its id (confused deputy).</summary>
    public string? OwnerPaneId { get; init; }

    /// <summary>
    /// The project this task works on (AC-320) — inherited from the session that delegated it, because a sub-agent
    /// asked to do a piece of its caller's work is working on the same thing the caller is. Null when that session
    /// has no project, which runs exactly as delegation always has.
    /// <para>
    /// Resolved once, when the task is accepted, and carried as a value: the start path hands it to the driver, where
    /// looking it up again would mean the UI thread from a thread that is waiting on the answer (AC-218).
    /// </para>
    /// </summary>
    public string? ProjectId { get; init; }

    public ISessionRuntime? Runtime { get; private set; }

    /// <summary>Fires when the task outlives what its profile allows; cancelled the moment the task ends, so a finished task is never stopped after the fact.</summary>
    public CancellationTokenSource? TimeoutCancellation { get; set; }

    /// <summary>Fires when a finished task's session has sat unused long enough to close; cancelled by a follow-up (which puts it back to work) or by a stop (which closes it anyway).</summary>
    public CancellationTokenSource? IdleCancellation { get; set; }

    public DelegatedTaskStatus Status { get; set; } = DelegatedTaskStatus.Queued;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public int TurnCount { get; set; }

    /// <summary>Tool calls requested in the current turn — reset at each turn boundary, so the false-success guard judges each turn on its own (AC-100).</summary>
    public int ToolCallsRequested { get; set; }

    /// <summary>Tool calls in the current turn that returned a non-error result (AC-100). Zero-while-requested is a no-op turn.</summary>
    public int ToolCallsSucceeded { get; set; }

    /// <summary>Tool calls in the current turn that came back as an error — a denial by the delegated gate counts here (AC-100).</summary>
    public int ToolCallsErrored { get; set; }

    public string? Result { get; private set; }

    public string? Error { get; private set; }

    public bool IsFinished => Status is DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped;

    private int _worktreeReleaseClaimed;

    /// <summary>
    /// Claims the one worktree release this task gets (AC-106), so only the first caller performs it. The paths that
    /// close a delegated session can overlap: an idle reap whose delay has already elapsed can no longer be cancelled
    /// by a stop arriving at that instant, and both would then hand the same checkout back at once. A task is never
    /// started again, so one release is all there is to hand out.
    /// </summary>
    public bool TryClaimWorktreeRelease() => Interlocked.Exchange(ref _worktreeReleaseClaimed, 1) == 0;

    public void Attach(ISessionRuntime runtime)
    {
        Runtime = runtime;
        _CancelIdle();
    }

    /// <summary>Lets go of the session once it has been closed. The task keeps its result — what it produced outlives the session that produced it.</summary>
    public void ReleaseSession()
    {
        _CancelIdle();
        Runtime = null;
    }

    /// <summary>
    /// Records the outcome. <paramref name="keepSessionAlive"/> distinguishes a task that answered (its session
    /// stays up so a follow-up turn is still possible) from one that was stopped or never started.
    /// </summary>
    public void Finish(DelegatedTaskStatus status, string? result, string? error, bool keepSessionAlive = false)
    {
        // The task is done, so its timeout must not fire later and stop a session that answered long ago.
        TimeoutCancellation?.Cancel();
        TimeoutCancellation?.Dispose();
        TimeoutCancellation = null;

        Status = status;
        Result = result ?? Result;
        Error = error;
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
        Error);
}
