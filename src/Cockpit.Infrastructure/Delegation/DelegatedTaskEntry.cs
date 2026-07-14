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
    }

    public string TaskId { get; } = Guid.NewGuid().ToString("N");

    public SessionProfile Profile { get; }

    public string Prompt { get; }

    public string? TaskType { get; }

    public string? Label { get; }

    public string? WorkingDirectory { get; }

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

    public string? Result { get; private set; }

    public string? Error { get; private set; }

    public bool IsFinished => Status is DelegatedTaskStatus.Completed or DelegatedTaskStatus.Failed or DelegatedTaskStatus.Stopped;

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
