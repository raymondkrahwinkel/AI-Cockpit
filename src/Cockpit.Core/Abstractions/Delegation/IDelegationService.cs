using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// The engine behind the orchestrator (#67): a running session asks it to hand a task to another profile, and it
/// enforces what that profile allows before a process is ever spawned. The MCP tool surface is a thin shell over
/// this — the rules live here, not in the tool definitions, so they hold however the engine is reached.
/// </summary>
public interface IDelegationService
{
    /// <summary>
    /// Raised whenever a task is created or changes state, so the cockpit's task view follows a delegated session
    /// live. Delegation runs real work in the background; the operator has to be able to see it and stop it —
    /// invisible background agents are exactly what this project does not do.
    /// </summary>
    event Action? TasksChanged;

    /// <summary>
    /// The profiles that may be delegated to, with what they say they are good for. A profile that has not opted
    /// in is absent — a calling agent cannot delegate to what it cannot see.
    /// </summary>
    Task<IReadOnlyList<DelegationTargetView>> ListTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a task on <paramref name="profileLabel"/>. Rejects rather than spawns when the profile is unknown,
    /// is not a target, does not accept the declared task type, or was handed a working directory it does not
    /// allow. When the profile (or the cockpit) is at its concurrency cap the task is accepted as
    /// <see cref="DelegatedTaskStatus.Queued"/> and started when a slot frees — never silently dropped and never
    /// left hanging.
    /// </summary>
    Task<DelegatedTaskView> DelegateAsync(DelegationRequest request, CancellationToken cancellationToken = default);

    /// <summary>The task's current state, without pulling its whole output.</summary>
    DelegatedTaskView? GetTask(string taskId);

    /// <summary>Every task this cockpit knows about, newest first.</summary>
    IReadOnlyList<DelegatedTaskView> ListTasks(DelegatedTaskStatus? status = null);

    /// <summary>
    /// The events produced since <paramref name="cursor"/>, for a caller that wants to watch progress rather than
    /// wait for the answer. Returns the cursor to pass next time, and whether the task is finished.
    /// </summary>
    (IReadOnlyList<SessionEvent> Events, int NextCursor, bool Done) GetOutput(string taskId, int cursor = 0);

    /// <summary>
    /// Continues a task with another turn on the same session. A task that has answered is Completed but still
    /// alive, so it can take a follow-up; one whose session is gone is refused with a reason rather than silently
    /// accepted — a follow-up that quietly does nothing leaves the caller waiting for a turn that never comes.
    /// </summary>
    Task<DelegatedTaskView> SendFollowUpAsync(string taskId, string text, CancellationToken cancellationToken = default);

    /// <summary>Stops the task and its session. Safe to call on an unknown or already-finished task.</summary>
    Task<DelegatedTaskView?> StopAsync(string taskId);
}

/// <summary>A profile a task may be handed to, as a calling agent sees it (#67).</summary>
public sealed record DelegationTargetView(
    string ProfileLabel,
    string Provider,
    string? Purpose,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AllowedTaskTypes,
    int MaxConcurrent,
    int RunningTasks);

/// <summary>What a caller asks for when delegating (#67). Everything else — driver, credentials, environment — comes from the profile, never from the call.</summary>
public sealed record DelegationRequest(
    string ProfileLabel,
    string Prompt,
    string? TaskType = null,
    string? Label = null,
    string? WorkingDirectory = null,
    int Depth = 0);
