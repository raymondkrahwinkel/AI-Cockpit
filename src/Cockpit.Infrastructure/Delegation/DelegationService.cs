using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Runs delegated tasks (#67) as headless sessions on the shared <see cref="ISessionManager"/>, and enforces the
/// target profile's <see cref="DelegationPolicy"/> before anything is spawned. Every rule that matters is checked
/// here rather than in the MCP tool layer: the tool surface is a shell, and a guard that lives in the shell is a
/// guard an agent can talk its way around by reaching the engine another way.
/// </summary>
internal sealed class DelegationService : IDelegationService, ISingletonService
{
    /// <summary>
    /// The ceiling across all profiles together. A per-profile cap protects one provider's usage pot; this one
    /// stops a fan-out of tasks across several profiles from turning a single agent's decision into a dozen
    /// concurrent processes.
    /// </summary>
    private const int GlobalMaxConcurrent = 4;

    /// <summary>How deep delegation may nest. A delegated task does not get the orchestrator tools at all, so this is the backstop, not the primary guard.</summary>
    private const int MaxDepth = 1;

    /// <summary>How many tasks may wait for a slot before the cockpit says no rather than growing a queue nobody watches.</summary>
    private const int MaxQueued = 8;

    private readonly ISessionProfileStore _profileStore;
    private readonly ISessionManager _sessionManager;
    private readonly List<DelegatedTaskEntry> _tasks = [];
    private readonly Lock _tasksLock = new();

    public DelegationService(ISessionProfileStore profileStore, ISessionManager sessionManager)
    {
        _profileStore = profileStore;
        _sessionManager = sessionManager;
    }

    /// <summary>Raised whenever a task is added or changes state, so a UI view can follow along without polling.</summary>
    public event Action? TasksChanged;

    public async Task<IReadOnlyList<DelegationTargetView>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _profileStore.LoadAsync(cancellationToken);

        return profiles
            .Where(profile => profile.DelegationPolicy.AllowedAsTarget)
            .Select(profile => new DelegationTargetView(
                profile.Label,
                profile.Provider.ToString(),
                profile.DelegationPolicy.Purpose,
                profile.DelegationPolicy.Tags ?? [],
                profile.DelegationPolicy.AllowedTaskTypes ?? [],
                profile.DelegationPolicy.MaxConcurrent,
                _CountRunning(profile.Label)))
            .ToList();
    }

    public async Task<DelegatedTaskView> DelegateAsync(DelegationRequest request, CancellationToken cancellationToken = default)
    {
        var profiles = await _profileStore.LoadAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, request.ProfileLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new DelegationRejectedException($"No profile named '{request.ProfileLabel}'.");

        var policy = profile.DelegationPolicy;
        _Guard(request, policy);

        var entry = new DelegatedTaskEntry(profile, request);
        lock (_tasksLock)
        {
            if (_tasks.Count(task => task.Status == DelegatedTaskStatus.Queued) >= MaxQueued)
            {
                throw new DelegationRejectedException("Too many tasks are already waiting for a slot.");
            }

            _tasks.Add(entry);
        }

        TasksChanged?.Invoke();

        // At the cap the task waits rather than being refused or, worse, started anyway: the caller gets an
        // honest "Queued" back and can decide what to do, and a freeing slot picks it up.
        if (_HasFreeSlot(profile.Label, policy))
        {
            await _StartAsync(entry);
        }

        return entry.ToView();
    }

    public DelegatedTaskView? GetTask(string taskId) => _Find(taskId)?.ToView();

    public IReadOnlyList<DelegatedTaskView> ListTasks(DelegatedTaskStatus? status = null)
    {
        lock (_tasksLock)
        {
            return _tasks
                .Where(task => status is null || task.Status == status)
                .OrderByDescending(task => task.CreatedAt)
                .Select(task => task.ToView())
                .ToList();
        }
    }

    public (IReadOnlyList<SessionEvent> Events, int NextCursor, bool Done) GetOutput(string taskId, int cursor = 0)
    {
        var entry = _Find(taskId);
        if (entry?.Runtime is null)
        {
            return ([], cursor, entry?.IsFinished ?? true);
        }

        var (events, nextCursor) = entry.Runtime.EventsSince(cursor);
        return (events, nextCursor, entry.IsFinished);
    }

    public async Task<DelegatedTaskView?> SendFollowUpAsync(string taskId, string text, CancellationToken cancellationToken = default)
    {
        var entry = _Find(taskId);
        if (entry?.Runtime is null || entry.IsFinished)
        {
            return entry?.ToView();
        }

        entry.Status = DelegatedTaskStatus.Running;
        TasksChanged?.Invoke();
        await entry.Runtime.SendUserMessageAsync(text, cancellationToken: cancellationToken);
        return entry.ToView();
    }

    public async Task<DelegatedTaskView?> StopAsync(string taskId)
    {
        var entry = _Find(taskId);
        if (entry is null)
        {
            return null;
        }

        if (entry.Runtime is not null)
        {
            await _sessionManager.StopAsync(entry.Runtime.Id);
        }

        entry.Finish(DelegatedTaskStatus.Stopped, result: entry.Result, error: null);
        TasksChanged?.Invoke();
        await _StartNextQueuedAsync(entry.Profile);
        return entry.ToView();
    }

    // Everything the target profile refuses is refused here, before a process exists. A caller cannot widen any
    // of it: the driver, the credentials and the environment all come from the profile, never from the call.
    private static void _Guard(DelegationRequest request, DelegationPolicy policy)
    {
        if (!policy.AllowedAsTarget)
        {
            throw new DelegationRejectedException($"Profile '{request.ProfileLabel}' is not available as a delegation target.");
        }

        if (request.Depth >= MaxDepth && !policy.MayDelegateFurther)
        {
            throw new DelegationRejectedException("A delegated task may not delegate further.");
        }

        if (policy.AllowedTaskTypes is { Count: > 0 } allowedTypes &&
            (request.TaskType is null || !allowedTypes.Contains(request.TaskType, StringComparer.OrdinalIgnoreCase)))
        {
            throw new DelegationRejectedException(
                $"Profile '{request.ProfileLabel}' only accepts these task types: {string.Join(", ", allowedTypes)}.");
        }

        if (request.WorkingDirectory is { Length: > 0 } workingDirectory && !_IsAllowedWorkingDirectory(workingDirectory, policy))
        {
            throw new DelegationRejectedException(
                $"Profile '{request.ProfileLabel}' does not allow a task to run in '{workingDirectory}'.");
        }
    }

    // Compared on the resolved full path, so "allowed/../../etc" cannot walk out of an allowed directory.
    private static bool _IsAllowedWorkingDirectory(string workingDirectory, DelegationPolicy policy)
    {
        if (policy.AllowedWorkingDirs is not { Count: > 0 } allowed)
        {
            return false;
        }

        var requested = Path.GetFullPath(workingDirectory);
        return allowed.Any(root =>
        {
            var allowedRoot = Path.GetFullPath(root);
            return requested.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
                   requested.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task _StartAsync(DelegatedTaskEntry entry)
    {
        try
        {
            var runtime = _sessionManager.Create(entry.Profile);
            entry.Attach(runtime);
            runtime.EventAppended += evt => _OnTaskEvent(entry, evt);

            // A delegated session has no human to answer a permission prompt, so it runs under the profile's
            // ceiling — never bypass, never a mode that would block waiting for a click that cannot come.
            await runtime.StartAsync(
                entry.Profile,
                entry.Profile.DelegationPolicy.PermissionCeiling,
                model: null,
                enabledMcpServerNames: null,
                workingDirectory: entry.WorkingDirectory);

            entry.Status = DelegatedTaskStatus.Running;
            entry.StartedAt = DateTimeOffset.Now;
            TasksChanged?.Invoke();

            await runtime.SendUserMessageAsync(entry.Prompt);
        }
        catch (Exception ex)
        {
            // A task that cannot start is a visibly failed task, not one that quietly sits at Queued forever.
            entry.Finish(DelegatedTaskStatus.Failed, result: null, error: ex.Message);
            TasksChanged?.Invoke();
        }
    }

    private void _OnTaskEvent(DelegatedTaskEntry entry, SessionEvent evt)
    {
        switch (evt)
        {
            case TurnCompleted turn:
                entry.TurnCount++;
                // The task is answered, but the session stays up: a caller can send a follow-up turn. It is torn
                // down on stop, which is also what the orchestrator does once it has the result it wanted.
                entry.Finish(
                    turn.IsError ? DelegatedTaskStatus.Failed : DelegatedTaskStatus.Completed,
                    result: entry.Runtime?.LastAssistantText,
                    error: turn.IsError ? turn.Result : null,
                    keepSessionAlive: true);
                TasksChanged?.Invoke();
                _ = _StartNextQueuedAsync(entry.Profile);
                break;

            case SessionError error:
                entry.Finish(DelegatedTaskStatus.Failed, result: null, error: error.Message);
                TasksChanged?.Invoke();
                _ = _StartNextQueuedAsync(entry.Profile);
                break;
        }
    }

    private async Task _StartNextQueuedAsync(SessionProfile profile)
    {
        DelegatedTaskEntry? next;
        lock (_tasksLock)
        {
            next = _tasks
                .Where(task => task.Status == DelegatedTaskStatus.Queued &&
                               string.Equals(task.Profile.Label, profile.Label, StringComparison.OrdinalIgnoreCase))
                .OrderBy(task => task.CreatedAt)
                .FirstOrDefault();
        }

        if (next is not null && _HasFreeSlot(profile.Label, profile.DelegationPolicy))
        {
            await _StartAsync(next);
        }
    }

    private bool _HasFreeSlot(string profileLabel, DelegationPolicy policy)
    {
        lock (_tasksLock)
        {
            var runningHere = _tasks.Count(task =>
                task.Status == DelegatedTaskStatus.Running &&
                string.Equals(task.Profile.Label, profileLabel, StringComparison.OrdinalIgnoreCase));
            var runningEverywhere = _tasks.Count(task => task.Status == DelegatedTaskStatus.Running);

            return runningHere < policy.MaxConcurrent && runningEverywhere < GlobalMaxConcurrent;
        }
    }

    private int _CountRunning(string profileLabel)
    {
        lock (_tasksLock)
        {
            return _tasks.Count(task =>
                task.Status == DelegatedTaskStatus.Running &&
                string.Equals(task.Profile.Label, profileLabel, StringComparison.OrdinalIgnoreCase));
        }
    }

    private DelegatedTaskEntry? _Find(string taskId)
    {
        lock (_tasksLock)
        {
            return _tasks.FirstOrDefault(task => task.TaskId == taskId);
        }
    }
}
