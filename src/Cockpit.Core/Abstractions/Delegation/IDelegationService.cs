using Cockpit.Core.Delegation;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Abstractions.Delegation;

/// <summary>
/// The engine behind the orchestrator (#67): a running session hands it a task for another profile, enforcing what
/// that profile allows before a process is spawned. The MCP tool surface is a thin shell — the rules live here.
/// A <c>callerPaneId</c> (AC-128), when set, scopes calls to its own task (confused deputy); null is unscoped.
/// </summary>
public interface IDelegationService
{
    /// <summary>
    /// Raised whenever a task is created or changes state, so the cockpit's task view follows a delegated session
    /// live. Delegation runs real work in the background; the operator has to be able to see and stop it —
    /// invisible background agents are exactly what this project does not do.
    /// </summary>
    event Action? TasksChanged;

    /// <summary>
    /// The profiles that may be delegated to, with what they say they are good for. A profile that has not opted
    /// in is absent — a calling agent cannot delegate to what it cannot see.
    /// </summary>
    Task<IReadOnlyList<DelegationTargetView>> ListTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records what a target profile turned out to be good for: purpose, tags, kinds of work accepted — knowledge an
    /// orchestrator learns by using it, lost if not written down. Deliberately the <em>only</em> thing a caller may
    /// change; capability (target status, permissions, dirs, concurrency, creds) stays the operator's; refused otherwise.
    /// </summary>
    Task<DelegationTargetView> DescribeTargetAsync(
        string profileLabel,
        string? purpose,
        IReadOnlyList<string>? tags,
        IReadOnlyList<string>? taskTypes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Scaffolds a new local-model profile (Ollama/LM Studio) and persists it (#67, AC-6), so a session can run under
    /// it and the operator can later enrol it as a delegation target. Created deliberately <em>not</em> as a target
    /// (<see cref="DelegationPolicy.AllowedAsTarget"/> false); rejects a blank/taken label, unknown provider, blank model.
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
    /// The providers a session can run under: local ones scaffoldable with <see cref="AddLocalModelProfileAsync"/>
    /// (Ollama, LM Studio) plus every plugin-registered provider. Each says whether it's addable this way, so a caller
    /// knows what it can set up itself versus what (a plugin provider may carry a login) is the operator's to create.
    /// </summary>
    IReadOnlyList<AvailableProviderView> ListProviders();

    /// <summary>
    /// Starts a task on <paramref name="profileLabel"/>. Rejects rather than spawns when the profile is unknown, not
    /// a target, doesn't accept the task type, or was handed a disallowed working directory. At the concurrency cap
    /// the task is accepted as <see cref="DelegatedTaskStatus.Queued"/> and started when a slot frees — never dropped.
    /// </summary>
    Task<DelegatedTaskView> DelegateAsync(DelegationRequest request, string? callerPaneId = null, CancellationToken cancellationToken = default);

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
    Task<DelegatedTaskView> SendFollowUpAsync(string taskId, string text, string? callerPaneId = null, CancellationToken cancellationToken = default);

    /// <summary>Stops the task and its session. Safe to call on an unknown or already-finished task.</summary>
    Task<DelegatedTaskView?> StopAsync(string taskId, string? callerPaneId = null);
}

// A profile a caller just scaffolded (#67, AC-6): added and usable to start a session under, but not yet a
// delegation target — enrolling it, and setting what a delegated task under it may do, is the operator's call.
public sealed record ScaffoldedProfileView(
    string Label,
    string Provider,
    string Model,
    string BaseUrl,
    string? Purpose,
    IReadOnlyList<string> Tags);

// A provider a session can run under, as a calling agent sees it (AC-6). `Name` is the value to
// pass where a provider is named (e.g. `add_profile`'s `provider` for the local ones);
// `AddableWithAddProfile` is false for a plugin/login provider the operator alone may set up.
public sealed record AvailableProviderView(
    string Name,
    string DisplayName,
    string Kind,
    bool AddableWithAddProfile);

// A profile a task may be handed to, as a calling agent sees it (#67). `McpServers` is the set
// of MCP servers a task delegated to this profile would actually receive — the caller reads it to pass a valid
// narrowing subset on `delegate_task` (AC-136) rather than guessing at server names.
public sealed record DelegationTargetView(
    string ProfileLabel,
    string Provider,
    string? Purpose,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> AllowedTaskTypes,
    int MaxConcurrent,
    int RunningTasks,
    IReadOnlyList<string> McpServers);

// What a caller asks for when delegating (#67). Everything else — driver, credentials, environment — comes from
// the profile, never from the call. `McpServers` (AC-136) optionally narrows the task to a subset
// of the servers the profile allows; null leaves the profile's full set, and a name outside the allowed set is
// refused, never honoured — the per-task choice can only restrict, never grant.
public sealed record DelegationRequest(
    string ProfileLabel,
    string Prompt,
    string? TaskType = null,
    string? Label = null,
    string? WorkingDirectory = null,
    string? RequestedPermission = null,
    int Depth = 0,
    IReadOnlyList<string>? McpServers = null);
