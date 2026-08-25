using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Consent;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Delegation;

// Runs delegated tasks (#67) as headless sessions on the shared `ISessionManager`, enforcing the
// target profile's `DelegationPolicy` here rather than in the MCP tool layer: the tool surface is a
// shell, and a guard living there is one an agent can talk its way around by reaching the engine another way.
internal sealed class DelegationService : IDelegationService, ILiveSessionSource, ISingletonService
{
    // The ceiling across all profiles together. A per-profile cap protects one provider's usage pot; this one
    // stops a fan-out of tasks across several profiles from turning a single agent's decision into a dozen
    // concurrent processes.
    private const int GlobalMaxConcurrent = 4;

    // How deep delegation may nest. A delegated task does not get the orchestrator tools at all, so this is the backstop, not the primary guard.
    private const int MaxDepth = 1;

    // How many tasks may wait for a slot before the cockpit says no rather than growing a queue nobody watches.
    private const int MaxQueued = 8;

    // How long a finished task's session is kept alive for a follow-up before the cockpit closes it. Not torn
    // down the moment a turn ends, since an orchestrator that never calls stop_task would otherwise leave a
    // sub-agent (a CLI process, an Ollama model) sitting idle in memory until the app closes.
    private static readonly TimeSpan IdleSessionWindow = TimeSpan.FromMinutes(5);

    // How long a finished task's entry — including its full `Result` text — is kept in `_tasks` (AC-880): an
    // hour, well past `IdleSessionWindow` and any reasonable poll interval, so `get_task_result` keeps working
    // without this becoming a second, longer-lived cache of its own.
    private static readonly TimeSpan TaskRetention = TimeSpan.FromHours(1);

    private readonly ISessionProfileStore _profileStore;
    private readonly ISessionManager _sessionManager;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly IDelegationAuditLog _auditLog;
    private readonly ISessionWorkspaces _workspaces;
    private readonly IPluginProviderRegistry? _providerRegistry;

    // How a task finds the project of the session that delegated it (AC-320). Absent in a test graph; the task then runs without a project, as delegation always did.
    private readonly ISessionProjectResolver? _projects;

    // Tears down the worktrees a finished task made for itself (AC-106). Absent in a test graph that does not exercise worktrees; the startup reconcile is the net then, as it was before.
    private readonly IWorktreeManager? _worktrees;

    // The operator's Approve/Deny gate (#AC-47), asked when a per-task `DelegationRequest.RequestedPermission`
    // exceeds the profile's ceiling (AC-117). Absent in a test graph or an off-path caller with no UI listening — that
    // degrades to the old behaviour, a silent clamp to the ceiling, never a widen nobody was there to approve.
    private readonly IConsentBroker? _consent;

    private readonly Func<int, TimeSpan> _timeout;
    private readonly TimeSpan _idleWindow;
    private readonly TimeSpan _taskRetention;
    private readonly List<DelegatedTaskEntry> _tasks = [];
    private readonly Lock _tasksLock = new();

    public DelegationService(
        ISessionProfileStore profileStore,
        ISessionManager sessionManager,
        IMcpServerStore mcpServerStore,
        IDelegationAuditLog auditLog,
        ISessionWorkspaces workspaces,
        IPluginProviderRegistry? providerRegistry = null,
        ISessionProjectResolver? projects = null,
        IWorktreeManager? worktrees = null,
        IConsentBroker? consent = null)
        : this(profileStore, sessionManager, mcpServerStore, auditLog, minutes => TimeSpan.FromMinutes(minutes), workspaces, providerRegistry, projects, worktrees, idleWindow: null, consent)
    {
    }

    // Test seam: lets a test express the profile's timeout, the idle window, and the task retention in
    // milliseconds, rather than waiting minutes (or an hour) for any of them.
    internal DelegationService(
        ISessionProfileStore profileStore,
        ISessionManager sessionManager,
        IMcpServerStore mcpServerStore,
        IDelegationAuditLog auditLog,
        Func<int, TimeSpan> timeout,
        ISessionWorkspaces? workspaces = null,
        IPluginProviderRegistry? providerRegistry = null,
        ISessionProjectResolver? projects = null,
        IWorktreeManager? worktrees = null,
        TimeSpan? idleWindow = null,
        IConsentBroker? consent = null,
        TimeSpan? taskRetention = null)
    {
        _profileStore = profileStore;
        _sessionManager = sessionManager;
        _mcpServerStore = mcpServerStore;
        _auditLog = auditLog;
        _workspaces = workspaces ?? NoSessionWorkspaces.Instance;
        _providerRegistry = providerRegistry;
        _projects = projects;
        _worktrees = worktrees;
        _timeout = timeout;
        _idleWindow = idleWindow ?? IdleSessionWindow;
        _consent = consent;
        _taskRetention = taskRetention ?? TaskRetention;
    }

    // The delegated tasks that still hold a session, as pane ids — a task's verified pane id is its task id
    // (see `_StartAsync`). A delegated session has no pane, so without this the worktree guards treated its
    // checkout as abandoned and both the operator's panel and an agent's `worktree_remove` could sweep it (AC-106).
    public IReadOnlySet<string> LiveSessionIds
    {
        get
        {
            lock (_tasksLock)
            {
                return _tasks
                    .Where(task => task.Runtime is not null)
                    .Select(task => task.TaskId)
                    .ToHashSet(StringComparer.Ordinal);
            }
        }
    }

    // Raised whenever a task is added or changes state, so a UI view can follow along without polling.
    public event Action? TasksChanged;

    public async Task<IReadOnlyList<DelegationTargetView>> ListTargetsAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _profileStore.LoadAsync(cancellationToken);
        var registry = await _mcpServerStore.LoadAsync(cancellationToken);

        return profiles
            .Where(profile => profile.DelegationPolicy.AllowedAsTarget)
            .Select(profile => new DelegationTargetView(
                profile.Label,
                profile.Provider.ToString(),
                profile.DelegationPolicy.Purpose,
                profile.DelegationPolicy.Tags ?? [],
                profile.DelegationPolicy.AllowedTaskTypes ?? [],
                profile.DelegationPolicy.MaxConcurrent,
                _CountRunning(profile.Label),
                _AvailableServers(registry, profile)))
            .ToList();
    }

    // The MCP servers a task delegated to `profile` would receive, sorted, as the
    // listing surfaces them so a caller can pass a valid `mcp_servers` narrowing on delegate_task (AC-136).
    private static IReadOnlyList<string> _AvailableServers(IReadOnlyList<McpServerConfig> registry, SessionProfile profile) =>
        [.. _NarrowServersFor(registry, profile, null).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    // Writes back what a profile turned out to be good for — and nothing else. Only the three descriptive fields
    // are touched; the rest of the policy is rebuilt from what the operator set, so a caller cannot make itself a
    // target, raise a ceiling, or open a directory this way. Same reason a non-target profile is refused here.
    public async Task<DelegationTargetView> DescribeTargetAsync(
        string profileLabel,
        string? purpose,
        IReadOnlyList<string>? tags,
        IReadOnlyList<string>? taskTypes,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _profileStore.LoadAsync(cancellationToken);
        var index = profiles.ToList().FindIndex(candidate => string.Equals(candidate.Label, profileLabel, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DelegationRejectedException($"No profile named '{profileLabel}'.");
        }

        var profile = profiles[index];
        if (!profile.DelegationPolicy.AllowedAsTarget)
        {
            throw new DelegationRejectedException(
                $"Profile '{profile.Label}' is not a delegation target, and enrolling one is the operator's call, not yours.");
        }

        // Null leaves a field as it was; a caller that knows only what a profile is good for should not have to
        // restate its task types to say so.
        var updated = profile.DelegationPolicy with
        {
            Purpose = purpose is null ? profile.DelegationPolicy.Purpose : _OrNull(purpose),
            Tags = tags is null ? profile.DelegationPolicy.Tags : _OrNull(tags),
            AllowedTaskTypes = taskTypes is null ? profile.DelegationPolicy.AllowedTaskTypes : _OrNull(taskTypes),
        };

        var saved = profiles.ToList();
        var savedProfile = profile with { Delegation = updated };
        saved[index] = savedProfile;
        await _profileStore.SaveAsync(saved, cancellationToken);

        var registry = await _mcpServerStore.LoadAsync(cancellationToken);
        return new DelegationTargetView(
            profile.Label,
            profile.Provider.ToString(),
            updated.Purpose,
            updated.Tags ?? [],
            updated.AllowedTaskTypes ?? [],
            updated.MaxConcurrent,
            _CountRunning(profile.Label),
            _AvailableServers(registry, savedProfile));
    }

    // The base URL a local provider defaults to when the caller does not give one.
    private const string OllamaDefaultBaseUrl = "http://localhost:11434";
    private const string LmStudioDefaultBaseUrl = "http://localhost:1234";

    // Adds a local-model profile and saves it — but never as a delegation target; enrolling a target and setting
    // its ceiling is the operator's call. Local only: an Ollama or LM Studio model carries no login, so scaffolding
    // one cannot leak a credential or spend a subscription — a Claude profile is the operator's to make.
    public async Task<ScaffoldedProfileView> AddLocalModelProfileAsync(
        string label,
        string provider,
        string model,
        string? baseUrl,
        string? purpose,
        IReadOnlyList<string>? tags,
        CancellationToken cancellationToken = default)
    {
        var trimmedLabel = _OrNull(label ?? string.Empty)
            ?? throw new DelegationRejectedException("A profile needs a label.");
        var trimmedModel = _OrNull(model ?? string.Empty)
            ?? throw new DelegationRejectedException("A profile needs a model id, e.g. 'qwen2.5-coder:7b'.");

        var profiles = await _profileStore.LoadAsync(cancellationToken);
        if (profiles.Any(candidate => string.Equals(candidate.Label, trimmedLabel, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DelegationRejectedException($"A profile named '{trimmedLabel}' already exists.");
        }

        var (config, resolvedBaseUrl) = _LocalProviderConfig(provider, trimmedModel, baseUrl);

        var suggestedPurpose = _OrNull(purpose ?? string.Empty);
        var policy = new DelegationPolicy(
            AllowedAsTarget: false,
            Purpose: suggestedPurpose,
            Tags: tags is null ? null : _OrNull(tags));

        var profile = new SessionProfile(
            trimmedLabel,
            ProviderConfig: config,
            Purpose: suggestedPurpose,
            Delegation: policy);

        await _profileStore.SaveAsync(profiles.Append(profile).ToList(), cancellationToken);

        return new ScaffoldedProfileView(
            profile.Label,
            profile.Provider.ToString(),
            trimmedModel,
            resolvedBaseUrl,
            policy.Purpose,
            policy.Tags ?? []);
    }

    // Every provider a session can run under: the two local ones a caller may scaffold with
    // `AddLocalModelProfileAsync`, then each provider a plugin registered — the operator's to set up since it
    // may carry a login. Lets a caller discover what exists, and which of it is theirs to add.
    public IReadOnlyList<AvailableProviderView> ListProviders()
    {
        var providers = new List<AvailableProviderView>
        {
            new("ollama", "Ollama", Kind: "local", AddableWithAddProfile: true),
            new("lmstudio", "LM Studio", Kind: "local", AddableWithAddProfile: true),
        };

        if (_providerRegistry is not null)
        {
            providers.AddRange(_providerRegistry.Registrations.Select(registration =>
                new AvailableProviderView(registration.ProviderId, registration.DisplayName, Kind: "plugin", AddableWithAddProfile: false)));
        }

        return providers;
    }

    // Maps the caller's provider name to a local HTTP provider config, or refuses. Only the local models are a
    // caller's to add; anything else (a Claude login and its credentials) is the operator's.
    private static (ProviderConfig Config, string BaseUrl) _LocalProviderConfig(string provider, string model, string? baseUrl)
    {
        switch (_OrNull(provider ?? string.Empty)?.ToLowerInvariant())
        {
            case "ollama":
            {
                var url = _OrNull(baseUrl ?? string.Empty) ?? OllamaDefaultBaseUrl;
                return (new OllamaConfig(url, model), url);
            }

            case "lmstudio" or "lm-studio" or "lm studio":
            {
                var url = _OrNull(baseUrl ?? string.Empty) ?? LmStudioDefaultBaseUrl;
                return (new LmStudioConfig(url, model), url);
            }

            default:
                throw new DelegationRejectedException(
                    $"'{provider}' is not a local model provider. Only 'ollama' and 'lmstudio' can be added this way — " +
                    "a Claude or other logged-in profile is the operator's to create.");
        }
    }

    // An empty string (or an empty list) is how a caller says "there is nothing to say here" — stored as absent
    // rather than as a blank that reads like a value.
    private static string? _OrNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string>? _OrNull(IReadOnlyList<string> values)
    {
        var kept = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();

        return kept.Count > 0 ? kept : null;
    }

    public async Task<DelegatedTaskView> DelegateAsync(DelegationRequest request, string? callerPaneId = null, CancellationToken cancellationToken = default)
    {
        var profiles = await _profileStore.LoadAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, request.ProfileLabel, StringComparison.OrdinalIgnoreCase))
            ?? throw new DelegationRejectedException($"No profile named '{request.ProfileLabel}'.");

        // Normalise the per-task MCP selection: an all-blank or empty list means no narrowing (the profile's full
        // set), not "no servers at all" — an agent passing [] almost never means to strip a sub-agent of its
        // files, shell and git. Mirrors how the descriptive list fields collapse an empty list to null (AC-136).
        if (request.McpServers is { } rawServers)
        {
            var cleaned = rawServers.Select(name => name.Trim()).Where(name => name.Length > 0).ToList();
            request = request with { McpServers = cleaned.Count > 0 ? cleaned : null };
        }

        var policy = profile.DelegationPolicy;
        try
        {
            _Guard(request, policy, callerPaneId);
        }
        catch (DelegationRejectedException ex)
        {
            // A refusal is the interesting half of the trail: it says what an agent tried to do and what stopped
            // it, which a log of successes alone would never show.
            await _Audit(DelegationAuditAction.Refused, profile.Label, null, request, ex.Message);
            throw;
        }

        // AC-136: a per-task MCP selection may only narrow within what the profile already gets. A name outside
        // that allowed set is an escalation attempt — a server the operator disabled, or the orchestrator without
        // MayDelegateFurther — and is refused, with the available set named, not silently honoured or dropped.
        if (request.McpServers is { } requestedServers)
        {
            var allowed = await _ToolsForAsync(profile);
            var disallowed = requestedServers.Where(name => !allowed.Contains(name)).ToList();
            if (disallowed.Count > 0)
            {
                var reason =
                    $"Task requested MCP server(s) that profile '{profile.Label}' cannot delegate: {string.Join(", ", disallowed)}. " +
                    $"Available: {(allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)))}.";
                await _Audit(DelegationAuditAction.Refused, profile.Label, null, request, reason);
                throw new DelegationRejectedException(reason);
            }
        }

        // A sub-agent inherits the project of the session that delegated to it (AC-320): it is doing a piece of
        // that session's work, so the servers, overrides and contributions its caller starts with are the ones
        // it needs too. Resolved here, once — the start path is where looking it up would cost the UI thread.
        var projectId = _projects is null || callerPaneId is null
            ? null
            : await _projects.ProjectIdOfAsync(callerPaneId, cancellationToken);

        var entry = new DelegatedTaskEntry(profile, request) { OwnerPaneId = callerPaneId, ProjectId = projectId };
        lock (_tasksLock)
        {
            _tasks.RemoveAll(task => task.IsFinished && task.FinishedAt is { } finishedAt && DateTimeOffset.Now - finishedAt > _taskRetention);

            if (_tasks.Count(task => task.Status == DelegatedTaskStatus.Queued) >= MaxQueued)
            {
                throw new DelegationRejectedException("Too many tasks are already waiting for a slot.");
            }

            _tasks.Add(entry);
        }

        TasksChanged?.Invoke();
        await _Audit(DelegationAuditAction.Delegated, profile.Label, entry.TaskId, request, reason: null);

        // At the cap the task waits rather than being refused or, worse, started anyway: the caller gets an
        // honest "Queued" back and can decide what to do, and a freeing slot picks it up.
        if (_HasFreeSlot(profile.Label, policy))
        {
            await _StartAsync(entry);
        }

        return entry.ToView();
    }

    public DelegatedTaskView? GetTask(string taskId, string? callerPaneId = null) => _Find(taskId, callerPaneId)?.ToView();

    public IReadOnlyList<DelegatedTaskView> ListTasks(DelegatedTaskStatus? status = null, string? callerPaneId = null)
    {
        lock (_tasksLock)
        {
            return _tasks
                .Where(task => status is null || task.Status == status)
                // AC-128: an agent lists only the tasks it created; a null caller (operator/UI/off-path) sees every one.
                .Where(task => callerPaneId is null || task.OwnerPaneId == callerPaneId)
                .OrderByDescending(task => task.CreatedAt)
                .Select(task => task.ToView())
                .ToList();
        }
    }

    public (IReadOnlyList<SessionEvent> Events, int NextCursor, bool Done) GetOutput(string taskId, int cursor = 0, string? callerPaneId = null)
    {
        var entry = _Find(taskId, callerPaneId);
        if (entry?.Runtime is null)
        {
            return ([], cursor, entry?.IsFinished ?? true);
        }

        var (events, nextCursor) = entry.Runtime.EventsSince(cursor);
        return (events, nextCursor, entry.IsFinished);
    }

    // Continues a task with another turn. A task that has answered is *Completed*, not gone: its session
    // is kept alive so the caller can follow up. A task whose session really is gone is refused loudly rather
    // than accepted into the void — a follow-up that silently does nothing is worse than an error.
    public async Task<DelegatedTaskView> SendFollowUpAsync(string taskId, string text, string? callerPaneId = null, CancellationToken cancellationToken = default)
    {
        var entry = _Find(taskId, callerPaneId)
            ?? throw new DelegationRejectedException($"No task '{taskId}'.");

        if (entry.Runtime is not { IsRunning: true })
        {
            throw new DelegationRejectedException(
                $"Task '{taskId}' has no live session to continue (it is {entry.Status}). Delegate a new task instead.");
        }

        // The concurrency cap counts work being done on a profile, not just tasks being started: a follow-up
        // puts that session back to work, so it must pass the same gate — it used to skip it, letting a follow-up
        // run alongside another task on a profile set to one at a time. Refused, not queued: no silent deferral.
        if (entry.Status != DelegatedTaskStatus.Running && !_HasFreeSlot(entry.Profile.Label, entry.Profile.DelegationPolicy))
        {
            throw new DelegationRejectedException(
                $"Profile '{entry.Profile.Label}' is already running as many tasks as it allows at once " +
                $"({entry.Profile.DelegationPolicy.MaxConcurrent}). Wait for one to finish, then send the follow-up.");
        }

        // The session is wanted after all, so the clock that would have closed it stops.
        entry.IdleCancellation?.Cancel();
        entry.IdleCancellation?.Dispose();
        entry.IdleCancellation = null;

        entry.Status = DelegatedTaskStatus.Running;
        TasksChanged?.Invoke();
        await entry.Runtime.SendUserMessageAsync(text, cancellationToken: cancellationToken);

        // The new turn gets the profile's time budget of its own; the old timer was cancelled when the previous
        // turn finished, so a follow-up is not silently running against an expired clock.
        _ArmTimeout(entry);
        await _Audit(DelegationAuditAction.FollowUp, entry.Profile.Label, entry.TaskId, request: null, reason: null, entry);
        return entry.ToView();
    }

    public async Task<DelegatedTaskView?> StopAsync(string taskId, string? callerPaneId = null)
    {
        var entry = _Find(taskId, callerPaneId);
        if (entry is null)
        {
            return null;
        }

        if (entry.Runtime is not null)
        {
            await _sessionManager.StopAsync(entry.Runtime.Id);
        }

        // AC-971: a task cut short still leaves whatever it had already written, so the reading is taken here too —
        // before the worktree goes back and there is nothing left to read.
        if (entry.WorkspaceBaseline is not null)
        {
            entry.ChangedPaths = DelegatedWorkspaceChanges.Added(
                entry.WorkspaceBaseline,
                await DelegatedWorkspaceChanges.SnapshotAsync(entry.WorkingDirectory));
        }

        entry.Finish(DelegatedTaskStatus.Stopped, result: entry.Result, error: null);
        await _ReleaseWorktreesAsync(entry);
        TasksChanged?.Invoke();
        await _Audit(DelegationAuditAction.Stopped, entry.Profile.Label, entry.TaskId, request: null, reason: null, entry);
        await _StartNextQueuedAsync(entry.Profile);
        return entry.ToView();
    }

    // Everything the target profile refuses is refused here, before a process exists. A caller cannot widen any
    // of it: the driver, the credentials and the environment all come from the profile, never from the call.
    private void _Guard(DelegationRequest request, DelegationPolicy policy, string? callerPaneId)
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

        if (request.WorkingDirectory is { Length: > 0 } workingDirectory && !_IsAllowedWorkingDirectory(workingDirectory, policy, callerPaneId))
        {
            // Name what *is* allowed, not just what was refused (AC-114): a caller cannot see the profile's
            // allowed dirs or the active-session dirs from the MCP surface, so a bare refusal leaves it guessing.
            var allowed = _AllowedWorkingDirectories(policy, callerPaneId);
            var where = allowed.Count == 0
                ? "This profile has no allowed working directories configured, and no cockpit session is currently " +
                  "working in one. Set the profile's allowed working directories, or delegate from a session that " +
                  "already works in the target directory."
                : $"Allowed here are: {string.Join(", ", allowed)} (and their subdirectories). Add more under the " +
                  "profile's delegation settings, or delegate from a session that works in the target directory.";
            throw new DelegationRejectedException(
                $"Profile '{request.ProfileLabel}' does not allow a task to run in '{workingDirectory}'. {where}");
        }
    }

    // The directories a delegated task may run in: the profile's own allow-list plus the dir the calling session is itself working in (AC-128 — scoped to the caller, not every open session). Surfaced in the rejection reason (AC-114) so a refused caller can see where it may go.
    private IReadOnlyList<string> _AllowedWorkingDirectories(DelegationPolicy policy, string? callerPaneId) =>
        [.. (policy.AllowedWorkingDirs ?? []).Concat(_CallerWorkspace(callerPaneId))];

    // AC-128: an agent may delegate into the directory ITS OWN session is working in — not any directory some
    // other open session happens to be in. The old union let a pane confined to /repoX place a sub-agent in
    // /repoY merely because an unrelated pane was open there. Off the verified path, the whole active set stands.
    private IReadOnlyList<string> _CallerWorkspace(string? callerPaneId)
    {
        if (callerPaneId is null)
        {
            return _workspaces.ActiveWorkingDirectories;
        }

        // A delegated (headless) caller has no UI tab — its verified pane id is its own task id — so fall back to
        // that task's own working directory. Without this, multi-level delegation is refused because the pane
        // lookup finds no UI session (AC-128 review follow-up).
        if (_workspaces.WorkingDirectoryForPane(callerPaneId) is { Length: > 0 } paneDirectory)
        {
            return [paneDirectory];
        }

        return _Find(callerPaneId)?.WorkingDirectory is { Length: > 0 } taskDirectory ? [taskDirectory] : [];
    }

    // Where a delegated task may run: the directories the target profile allows, and the ones the cockpit's own
    // sessions are already working in. The second is what makes delegation usable — the sub-agent reaches
    // nothing its caller did not already have. Everywhere else still needs the profile's own say-so.
    private bool _IsAllowedWorkingDirectory(string workingDirectory, DelegationPolicy policy, string? callerPaneId)
    {
        var allowed = _AllowedWorkingDirectories(policy, callerPaneId);

        if (allowed.Count == 0)
        {
            return false;
        }

        // Compared on the resolved full path, so "allowed/../../etc" cannot walk out of an allowed directory.
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
        // See TryClaimStart: while the consent wait below is in flight, entry.Status is still Queued, so the queue
        // drainer must not be allowed to pick this same entry a second time and start it twice.
        if (!entry.TryClaimStart())
        {
            return;
        }

        // AC-575/AC-89: restamps the ambient McpRequestContext to *this task's* owner. Without it, the queue drainer
        // would run another owner's start under the stopper's identity — letting a task queued by pane X start
        // above its ceiling on the assistant's AC-575 bypass, audited in the assistant's name.
        McpRequestContext.Set(entry.OwnerPaneId);

        try
        {
            // A delegated session has no human to answer a prompt, so it runs under the profile's ceiling by
            // default — never bypass. A request ABOVE the ceiling may be asked instead (see _EffectiveCeilingAsync).
            // Resolved before Running is set, so a task awaiting that answer never occupies a concurrency slot.
            var effectiveCeiling = await _EffectiveCeilingAsync(entry);
            entry.EffectiveCeiling = effectiveCeiling;

            // AC-971: what the working directory already had lying around, so the report at the end names what this
            // task changed rather than what the delegating session left dirty. Taken before the session exists.
            entry.WorkspaceBaseline = await DelegatedWorkspaceChanges.SnapshotAsync(entry.WorkingDirectory);

            var runtime = _sessionManager.Create(entry.Profile);
            entry.Attach(runtime);

            // Mark it running *before* the pump can deliver anything. A fast session can complete its turn while
            // this method is still unwinding, and setting Running afterwards would overwrite the Completed the
            // event handler had already recorded — leaving a finished task reported as still working.
            entry.Status = DelegatedTaskStatus.Running;
            entry.StartedAt = DateTimeOffset.Now;
            TasksChanged?.Invoke();

            runtime.EventAppended += evt => _OnTaskEvent(entry, evt);

            await runtime.StartAsync(
                entry.Profile,
                effectiveCeiling,
                model: null,
                enabledMcpServerNames: await _ToolsForAsync(entry.Profile, entry.McpServers),
                workingDirectory: entry.WorkingDirectory,
                // AC-128/AC-89: give the delegated session its own verified MCP identity, keyed on the task id, so
                // the driver mints it a per-session token instead of the shared app key. Without this a sub-agent's
                // own orchestrator calls arrive unscoped and could reach every session's tasks (confused deputy).
                launchOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [WellKnownPluginSessionOptions.PaneId] = entry.TaskId,
                    // AC-378: nobody is watching a delegated task, so its narrowing must be authoritative — the
                    // driver hands the CLI exactly the servers _ToolsForAsync resolved, never the operator's own
                    // user/project config. A server resolving to nothing used to yield every account connector instead.
                    [WellKnownPluginSessionOptions.Unattended] = "true",
                },
                // The project this task inherited from the session that delegated it (AC-320), so its MCP fan-out
                // resolves the registry as that project sees it (AC-218) rather than unscoped. A value, never a
                // lookup: this runs where the driver may resolve synchronously.
                projectId: entry.ProjectId);

            // A local-model session treats permissionMode as a no-op and gates every MCP call through the
            // interactive PermissionRequested flow; with no human to answer it would hang until timeout (AC-78).
            // So it decides for itself: AutoApproveTools allows everything, otherwise each call is gated (AC-79).
            if (entry.Profile.Defaults?.AutoApproveTools == true)
            {
                await runtime.SetAutoApproveToolsAsync(true);
            }
            else
            {
                await runtime.SetDelegatedToolGateAsync(
                    effectiveCeiling,
                    entry.Profile.DelegationPolicy.AllowedTools ?? []);
            }

            await runtime.SendUserMessageAsync(entry.Prompt);
            _ArmTimeout(entry);
        }
        catch (Exception ex)
        {
            // A task that cannot start is a visibly failed task, not one that quietly sits at Queued forever. It
            // usually has no worktree to hand back — it never got far enough to ask for one — but a start that failed
            // late enough to have run is exactly the case where assuming that would be wrong.
            entry.Finish(DelegatedTaskStatus.Failed, result: null, error: ex.Message);
            await _ReleaseWorktreesAsync(entry);
            TasksChanged?.Invoke();
            await _Audit(DelegationAuditAction.Failed, entry.Profile.Label, entry.TaskId, request: null, ex.Message, entry);
        }
    }

    // The permission ceiling this task's session actually runs under (AC-117). A request within the profile's
    // ceiling is honoured outright; one ABOVE it goes to the operator's Approve/Deny gate (#AC-47) as a one-time
    // `ConsentRisk.Dangerous` consent, never remembered — a prompt-injected agent must never make it standing.
    private async Task<string> _EffectiveCeilingAsync(DelegatedTaskEntry entry)
    {
        var requested = entry.RequestedPermission;
        var profileCeiling = entry.Profile.DelegationPolicy.PermissionCeiling;

        // AC-971: a task whose caller asked for nothing runs READ-ONLY, not at whatever the profile allows — which
        // for a coder profile is bypassPermissions, so a task told to "only read and report" was handed the right to
        // rewrite the repository because nobody said otherwise. Still clamped by the profile's own ceiling.
        if (string.IsNullOrWhiteSpace(requested))
        {
            return DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(profileCeiling, DelegatedToolPermissionPolicy.ReadOnlyCeiling);
        }

        var clamped = DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(profileCeiling, requested);
        if (!DelegatedToolPermissionPolicy.IsAboveCeiling(profileCeiling, requested) || _consent is null)
        {
            return clamped;
        }

        var minutes = entry.Profile.DelegationPolicy.TimeoutMinutes;
        using var timeoutCts = minutes > 0 ? new CancellationTokenSource(_timeout(minutes)) : null;

        var decision = await _consent
            .RequestConsentAsync(_ElevationPrompt(entry, profileCeiling, requested), timeoutCts?.Token ?? default)
            .ConfigureAwait(false);
        await _Audit(
            decision.IsApproved ? DelegationAuditAction.PermissionElevated : DelegationAuditAction.PermissionElevationDenied,
            entry.Profile.Label,
            entry.TaskId,
            request: null,
            reason: decision.IsApproved
                ? $"Approved to run at '{requested}' instead of the profile ceiling '{profileCeiling}'."
                : $"Not approved; the task ran clamped to the profile ceiling '{clamped}'.",
            entry);

        return decision.IsApproved ? requested : clamped;
    }

    // The prompt names the one thing being asked for: the permission jump, and the working directory the elevated
    // session would act on (the one concrete fact that makes "bypassPermissions" mean something) — never the task's
    // own caller-controlled prompt or label, which is untrusted text a prompt-injected agent chooses.
    private static ConsentRequest _ElevationPrompt(DelegatedTaskEntry entry, string profileCeiling, string requested) =>
        new(
            "A delegated task wants to run above its profile's permission ceiling",
            $"Profile '{entry.Profile.Label}' asked to run a task at permission '{requested}' — above its configured " +
            $"ceiling '{profileCeiling}'{(entry.WorkingDirectory is { Length: > 0 } dir ? $", in '{dir}'" : string.Empty)}. " +
            $"Approving lets this one task run at '{requested}'; denying clamps it to '{profileCeiling}'.",
            new ConsentSource(entry.OwnerPaneId, null, ConsentSourceCatalog.Orchestrator),
            $"delegation.permission:{entry.Profile.Label}",
            ConsentRisk.Dangerous,
            AllowRemember: false);

    // Closes a finished task's session once nobody has followed up on it for `IdleSessionWindow`. Without this
    // a delegated session lived until the app did — an orchestrator that has its answer never calls stop_task.
    // The result is kept; only the session and the worktree it worked in go.
    private void _ArmIdleReap(DelegatedTaskEntry entry)
    {
        var idle = new CancellationTokenSource();
        entry.IdleCancellation = idle;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_idleWindow, idle.Token);
            }
            catch (OperationCanceledException)
            {
                // A follow-up came, or the task was stopped — either way this session is somebody else's business now.
                return;
            }

            // Still finished and still holding a session: nobody wants it any more.
            if (entry.Runtime is { } runtime && entry.IsFinished)
            {
                await _sessionManager.StopAsync(runtime.Id);
                entry.ReleaseSession();
                await _ReleaseWorktreesAsync(entry);
                TasksChanged?.Invoke();
            }
        });
    }

    // Hands back the worktrees a delegated task made for itself, now that its session is gone (AC-106) — the same
    // cleanup policy as `CloseSessionAsync`. Called from every ending path except a driver error (not proof the
    // session is over). Claimed and best-effort: a stuck worktree is left for reconcile, not a failed stop_task.
    private async Task _ReleaseWorktreesAsync(DelegatedTaskEntry entry)
    {
        // Absent manager first, on purpose: a graph without one must not spend the task's single claim on a release
        // that was never going to happen. Swapping these two would be silent, and would only show up as a worktree
        // that is never handed back.
        if (_worktrees is null || !entry.TryClaimWorktreeRelease())
        {
            return;
        }

        try
        {
            await _worktrees.ReleaseAsync(entry.TaskId);
        }
        catch (Exception)
        {
            // Left for the startup reconcile.
        }
    }

    // Stops a task that outlives what its profile allows. Nobody is watching a delegated session, so a model
    // that loops or waits forever would otherwise hold the profile's slot until the app closes. Cancelled the
    // moment the task ends, so a finished task is never stopped after the fact.
    private void _ArmTimeout(DelegatedTaskEntry entry)
    {
        var minutes = entry.Profile.DelegationPolicy.TimeoutMinutes;
        if (minutes <= 0)
        {
            return;
        }

        var timeout = new CancellationTokenSource();
        entry.TimeoutCancellation = timeout;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_timeout(minutes), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The task finished in time — the ordinary case.
                return;
            }

            if (entry.IsFinished)
            {
                return;
            }

            if (entry.Runtime is not null)
            {
                await _sessionManager.StopAsync(entry.Runtime.Id);
            }

            var reason = $"The task ran longer than the {minutes} minute(s) '{entry.Profile.Label}' allows and was stopped.";
            entry.Finish(DelegatedTaskStatus.Failed, result: entry.Result, error: reason);
            await _ReleaseWorktreesAsync(entry);
            TasksChanged?.Invoke();
            await _Audit(DelegationAuditAction.TimedOut, entry.Profile.Label, entry.TaskId, request: null, reason, entry);
            await _StartNextQueuedAsync(entry.Profile);
        });
    }

    private Task _Audit(
        DelegationAuditAction action,
        string profileLabel,
        string? taskId,
        DelegationRequest? request,
        string? reason,
        DelegatedTaskEntry? entry = null) =>
        _auditLog.RecordAsync(new DelegationAuditEntry(
            DateTimeOffset.Now,
            action,
            profileLabel,
            taskId,
            request?.Label ?? entry?.Label,
            request?.TaskType ?? entry?.TaskType,
            request?.Prompt ?? entry?.Prompt,
            reason));

    // The MCP servers a delegated session gets: everything the operator enabled, narrowed by the profile's
    // pre-selection and the caller's per-task selection, minus the orchestrator unless MayDelegateFurther —
    // a second lock on the recursion guard alongside `_Guard`'s depth check.
    internal async Task<IReadOnlySet<string>> _ToolsForAsync(SessionProfile profile, IReadOnlyList<string>? perTaskSelection = null)
    {
        var registry = await _mcpServerStore.LoadAsync();
        return _NarrowServersFor(registry, profile, perTaskSelection);
    }

    // The pure narrowing behind `_ToolsForAsync`: enabled registry servers, intersected with the profile's
    // pre-selection (AC-133/AC-130) and the caller's per-task selection (AC-136) when set, minus the orchestrator
    // unless MayDelegateFurther. Both intersections only ever remove, so a session is narrowed but never widened.
    internal static IReadOnlySet<string> _NarrowServersFor(
        IReadOnlyList<McpServerConfig> registry, SessionProfile profile, IReadOnlyList<string>? perTaskSelection)
    {
        var names = registry
            .Where(server => server.Enabled)
            .Select(server => server.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (profile.EnabledMcpServerNames is { } profileSelection)
        {
            names.IntersectWith(profileSelection);
        }

        if (perTaskSelection is { } perTask)
        {
            names.IntersectWith(perTask);
        }

        if (!profile.DelegationPolicy.MayDelegateFurther)
        {
            names.Remove(OrchestratorMcpServer.ServerName);
        }

        return names;
    }

    private void _OnTaskEvent(DelegatedTaskEntry entry, SessionEvent evt)
    {
        switch (evt)
        {
            case ToolUseRequested:
                entry.ToolCallsRequested++;
                break;

            case ToolResult toolResult:
                // A denial by the delegated permission gate comes back to the model as an error tool result, not an
                // exception — so counting error results is how the orchestrator sees that a tool call did not land.
                if (toolResult.IsError)
                {
                    entry.ToolCallsErrored++;
                }
                else
                {
                    entry.ToolCallsSucceeded++;
                }

                break;

            case TurnCompleted turn:
                entry.TurnCount++;

                // False-success guard (AC-100/AC-110): the local-model driver reports a turn "success" whenever the
                // HTTP stream ends cleanly, even if every tool call was denied or errored and nothing landed. Such
                // a turn is surfaced as Failed with a diagnostic; a turn with no tool calls at all stays Completed.
                var ranToolsButNoneSucceeded = entry.ToolCallsRequested > 0 && entry.ToolCallsSucceeded == 0;
                var isFailure = turn.IsError || ranToolsButNoneSucceeded;
                var diagnostic = turn.IsError
                    ? turn.Result
                    : ranToolsButNoneSucceeded
                        ? $"No-op run: {entry.ToolCallsErrored} of {entry.ToolCallsRequested} tool call(s) were blocked or errored and none succeeded, so the task produced no tool-made change. The delegated model replied: {entry.Runtime?.LastAssistantText}"
                        : null;

                // Per-turn, not per-session: clear the counters now this turn is classified, so a follow-up turn
                // is judged on its own tool calls. Without this a follow-up would inherit the prior turn's
                // false-failure or false-success (AC-100 review).
                entry.ToolCallsRequested = 0;
                entry.ToolCallsSucceeded = 0;
                entry.ToolCallsErrored = 0;

                // AC-971: with a workspace to read, the host takes stock before it reports the task done — the report
                // has to be in hand when the caller first sees a finished task. Without one there is nothing to wait
                // for, and the task is reported from this handler exactly as it always was.
                if (entry.WorkspaceBaseline is null)
                {
                    _FinishTurn(entry, isFailure, diagnostic);
                }
                else
                {
                    _ = _FinishTurnWithChangeReportAsync(entry, isFailure, diagnostic);
                }

                break;

            // Deliberately no worktree release here, unlike every other ending path. A SessionError is not proof
            // the session is over — some are notices from a session running fine (an events gap, a system-prompt
            // failure) — and releasing on one could delete a live sub-agent's working directory. Left for reconcile.
            case SessionError error:
                entry.Finish(DelegatedTaskStatus.Failed, result: null, error: error.Message);
                TasksChanged?.Invoke();
                _ = _Audit(DelegationAuditAction.Failed, entry.Profile.Label, entry.TaskId, request: null, error.Message, entry);
                _ = _StartNextQueuedAsync(entry.Profile);
                break;
        }
    }

    // Reports a finished turn: the task keeps its session for a follow-up, the queue moves on, and the audit line
    // says how it ended. Split out of `_OnTaskEvent` so the changed-path report can be awaited first (AC-971)
    // without the no-workspace case paying for a round trip it has nothing to read.
    private void _FinishTurn(DelegatedTaskEntry entry, bool isFailure, string? diagnostic)
    {
        // The task is answered, but the session stays up for a while: a caller can send a follow-up turn.
        // It is torn down on stop — and, when nobody stops it, once the idle window closes.
        entry.Finish(
            isFailure ? DelegatedTaskStatus.Failed : DelegatedTaskStatus.Completed,
            result: entry.Runtime?.LastAssistantText,
            error: diagnostic,
            keepSessionAlive: true);
        _ArmIdleReap(entry);
        TasksChanged?.Invoke();
        _ = _Audit(
            isFailure ? DelegationAuditAction.Failed : DelegationAuditAction.Completed,
            entry.Profile.Label, entry.TaskId, request: null, diagnostic, entry);
        _ = _StartNextQueuedAsync(entry.Profile);
    }

    // The same, with the host's own account of what the task changed attached first (AC-971). A read-only task that
    // changed files is failed on that ground whatever it said about itself: something got past the gate, and a
    // delegating session told "done" while its checkout has quietly moved is the failure this exists to end.
    private async Task _FinishTurnWithChangeReportAsync(DelegatedTaskEntry entry, bool isFailure, string? diagnostic)
    {
        // The reading is evidence, not a gate: SnapshotAsync answers null for anything it could not read, so a task
        // that answered is still reported as finished — with "could not be established" rather than a lost result.
        var after = await DelegatedWorkspaceChanges.SnapshotAsync(entry.WorkingDirectory);
        entry.ChangedPaths = DelegatedWorkspaceChanges.Added(entry.WorkspaceBaseline, after);

        if (entry.ChangedPaths is { Count: > 0 } changed && !DelegatedToolPermissionPolicy.AllowsChanges(entry.EffectiveCeiling))
        {
            isFailure = true;
            diagnostic =
                $"Out of scope: this task ran read-only (permission '{entry.EffectiveCeiling}'), but " +
                $"{changed.Count} path(s) in its working directory changed while it ran: " +
                $"{string.Join(", ", changed.Take(20))}{(changed.Count > 20 ? ", …" : string.Empty)}. " +
                "Review those changes before trusting this task's answer, and delegate with a write permission if " +
                "the work was meant to change files." +
                (diagnostic is { Length: > 0 } ? $" (Also: {diagnostic})" : string.Empty);
        }

        _FinishTurn(entry, isFailure, diagnostic);
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

    // Scope a task lookup to the pane that created it (AC-128): a task only exists for its owner, so an agent cannot
    // read, continue, or stop another session's task by naming its id (confused deputy). A null caller — the
    // operator/UI, or the off-path in-process loop where no middleware set a verified pane — sees every task.
    private DelegatedTaskEntry? _Find(string taskId, string? callerPaneId = null)
    {
        lock (_tasksLock)
        {
            var entry = _tasks.FirstOrDefault(task => task.TaskId == taskId);
            return entry is not null && callerPaneId is not null && entry.OwnerPaneId != callerPaneId ? null : entry;
        }
    }
}
