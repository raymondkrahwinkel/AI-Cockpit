using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// The engine behind the orchestrator (#67): a running session asks it to hand a task to another profile, and it
/// enforces what that profile allows before a process is ever spawned. The MCP tool surface is a thin shell over
/// this — the rules live here, not in the tool definitions, so they hold however the engine is reached.
/// <para>
/// Task-addressed calls take a <c>callerPaneId</c> — the transport-verified pane behind the request (AC-128). When
/// set, a task is only reachable by the pane that created it, so an agent cannot read, continue, stop, or list
/// another session's task by naming its id (confused deputy). A null caller — the operator/UI, or the off-path
/// in-process loop where no middleware set a verified pane — is unscoped and sees every task.
/// </para>
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
    /// Records what a target profile turned out to be good for: its purpose, its tags, and the kinds of work it
    /// accepts. An orchestrator learns this by using a profile — that Qwen reviews a frontend diff well and loses the
    /// thread on architecture is knowledge the run produced, and knowledge that is not written down is knowledge the
    /// next session does not have.
    /// <para>
    /// Deliberately the <em>only</em> thing a caller may change about a profile. Everything that decides what a
    /// delegated session can do — whether the profile is a target at all, its permission ceiling, the directories it
    /// may work in, how many tasks it will run at once, its credentials — stays with the operator. A caller that could
    /// set those could make itself a target that may do anything, anywhere, and every guard in this engine would be a
    /// suggestion. Refused for a profile that is not already a delegation target.
    /// </para>
    /// </summary>
    Task<DelegationTargetView> DescribeTargetAsync(
        string profileLabel,
        string? purpose,
        IReadOnlyList<string>? tags,
        IReadOnlyList<string>? taskTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scaffolds a new local-model profile (Ollama or LM Studio) and persists it (#67, AC-6), so a session can run
    /// under it and the operator can later enrol it as a delegation target. Created deliberately <em>not</em> as a
    /// target — <see cref="DelegationPolicy.AllowedAsTarget"/> stays false: what a delegated session may do (its
    /// permission ceiling, the directories it may work in, how many tasks at once) is the operator's to set, so a
    /// caller can add a local model but never make itself something it may delegate to. Only local providers may be
    /// added this way; a logged-in provider carries credentials and is the operator's to create.
    /// Rejects a blank label, a label already taken, an unknown provider, or a blank model.
    /// </summary>
    Task<ScaffoldedProfileView> AddLocalModelProfileAsync(
        string label,
        string provider,
        string model,
        string? baseUrl,
        string? purpose,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The providers a session can run under: the local ones a caller may scaffold with
    /// <see cref="AddLocalModelProfileAsync"/> (Ollama, LM Studio) and every provider a plugin registered. Each says
    /// whether it is addable this way, so a caller knows which it can set up itself and which are the operator's to
    /// create (a plugin provider may carry a login, and a logged-in provider is never a caller's to add).
    /// </summary>
    IReadOnlyList<AvailableProviderView> ListProviders();

    /// <summary>
    /// Starts a task on <paramref name="profileLabel"/>. Rejects rather than spawns when the profile is unknown,
    /// is not a target, does not accept the declared task type, or was handed a working directory it does not
    /// allow. When the profile (or the cockpit) is at its concurrency cap the task is accepted as
    /// <see cref="DelegatedTaskStatus.Queued"/> and started when a slot frees — never silently dropped and never
    /// left hanging.
    /// </summary>
    Task<DelegatedTaskView> DelegateAsync(DelegationRequest request, CancellationToken cancellationToken = default, string? callerPaneId = null);

    /// <summary>The task's current state, without pulling its whole output.</summary>
    DelegatedTaskView? GetTask(string taskId, string? callerPaneId = null);

    /// <summary>Every task this cockpit knows about, newest first.</summary>
    IReadOnlyList<DelegatedTaskView> ListTasks(DelegatedTaskStatus? status = null, string? callerPaneId = null);

    /// <summary>
    /// The events produced since <paramref name="cursor"/>, for a caller that wants to watch progress rather than
    /// wait for the answer. Returns the cursor to pass next time, and whether the task is finished.
    /// </summary>
    (IReadOnlyList<SessionEvent> Events, int NextCursor, bool Done) GetOutput(string taskId, int cursor = 0, string? callerPaneId = null);

    /// <summary>
    /// Continues a task with another turn on the same session. A task that has answered is Completed but still
    /// alive, so it can take a follow-up; one whose session is gone is refused with a reason rather than silently
    /// accepted — a follow-up that quietly does nothing leaves the caller waiting for a turn that never comes.
    /// </summary>
    Task<DelegatedTaskView> SendFollowUpAsync(string taskId, string text, CancellationToken cancellationToken = default, string? callerPaneId = null);

    /// <summary>Stops the task and its session. Safe to call on an unknown or already-finished task.</summary>
    Task<DelegatedTaskView?> StopAsync(string taskId, string? callerPaneId = null);
}

/// <summary>
/// A profile a caller just scaffolded (#67, AC-6): added and usable to start a session under, but not yet a
/// delegation target — enrolling it, and setting what a delegated task under it may do, is the operator's call.
/// </summary>
public sealed record ScaffoldedProfileView(
    string Label,
    string Provider,
    string Model,
    string BaseUrl,
    string? Purpose,
    IReadOnlyList<string> Tags);

/// <summary>
/// A provider a session can run under, as a calling agent sees it (AC-6). <paramref name="Name"/> is the value to
/// pass where a provider is named (e.g. <c>add_profile</c>'s <c>provider</c> for the local ones);
/// <paramref name="AddableWithAddProfile"/> is false for a plugin/login provider the operator alone may set up.
/// </summary>
public sealed record AvailableProviderView(
    string Name,
    string DisplayName,
    string Kind,
    bool AddableWithAddProfile);

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
    string? RequestedPermission = null,
    int Depth = 0);
