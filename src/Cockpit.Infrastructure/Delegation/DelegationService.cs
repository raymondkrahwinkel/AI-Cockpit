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

// Runs delegated tasks (#67) as headless sessions on the shared `ISessionManager`, and enforces the
// target profile's `DelegationPolicy` before anything is spawned. Every rule that matters is checked
// here rather than in the MCP tool layer: the tool surface is a shell, and a guard that lives in the shell is a
// guard an agent can talk its way around by reaching the engine another way.
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

    // How long a finished task's session is kept alive for a follow-up before the cockpit closes it. The session is
    // deliberately not torn down the moment a turn ends — the caller may want to ask one more thing, and a
    // conversation that has to be started again is not a conversation. But an orchestrator that simply never calls
    // stop_task (the common case: it has its answer and moves on) would leave a sub-agent sitting there until the
    // app closes — a CLI process, or an Ollama model held in memory, doing nothing at all. So it reaps itself, and
    // a follow-up within the window puts it back to work and starts the clock again.
    private static readonly TimeSpan IdleSessionWindow = TimeSpan.FromMinutes(5);

    // How long a finished task's entry — including its full `Result` text — is kept in `_tasks` after it
    // finished, before it is swept away (AC-880). `_tasks` only ever grows otherwise, since nothing else removes
    // an entry. An hour is well past `IdleSessionWindow` and any reasonable poll interval, so `get_task_result`
    // keeps working for the case it exists for — a caller that has not collected its answer yet — without this
    // becoming a second, longer-lived cache of its own.
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
    // (see `_StartAsync`). A delegated session has no pane, so without this the cockpit's live-session
    // registry never knew it was running and the worktree guards treated its checkout as abandoned: the operator's
    // panel offered to sweep it and an agent's `worktree_remove` let it go (AC-106).
    //
    // "Holds a session" and not "is running", deliberately: a task that has answered keeps its session for a
    // follow-up turn (`IdleSessionWindow`), and a follow-up puts it straight back to work in that same
    // directory.
    //
    // The guard is let go one step before the checkout is gone, not at the same moment: a closing path drops the
    // session and then hands the worktree back, so while that release runs the task is already absent from here. The
    // actors that could use that gap are the worktree panel's Remove and Clean-up-finished and an agent's
    // `worktree_remove`; the session is stopped by then, and the agent route is refused a step earlier by the
    // ownership check. Left as it is rather than carried across the release on a second piece of state.
    //
    // One ending is not covered: a task the driver reported an error on keeps its worktree but stops being listed
    // here, because `Finish` drops the session even though that error may not have ended it (see the
    // `SessionError` case). Until the next startup reconcile that checkout is unguarded — no worse than before
    // any of this, since a delegated task was never listed at all, but not fixed by it either.
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

    // Writes back what a profile turned out to be good for — and nothing else. Only the three descriptive fields are
    // touched; the rest of the policy is rebuilt from what the operator set, so a caller cannot make itself a target,
    // raise a ceiling, or open a directory by calling this. A profile that is not already a target is refused, for
    // the same reason: it is not a caller's to enrol.
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

    // Adds a local-model profile and saves it — but never as a delegation target. The soft purpose/tags a caller
    // suggests are carried, so the operator's later opt-in starts from them; the hard policy stays default and off
    // (`DelegationPolicy.AllowedAsTarget` false), because enrolling a target and setting its ceiling is
    // the operator's call. Local only: an Ollama or LM Studio model runs here and carries no login, so scaffolding
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
    // `AddLocalModelProfileAsync`, then each provider a plugin registered — the operator's to set up,
    // since such a provider may carry a login. So a caller can discover what exists — and which of it is theirs to
    // add — instead of guessing provider names or finding out only when add_profile refuses.
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

        // Normalise the per-task MCP selection: trim names, drop blanks, and treat an all-blank or empty list as
        // no narrowing (the profile's full set) rather than "no servers at all" — an agent passing [] almost never
        // means to strip a sub-agent of its files, shell and git. Mirrors how the descriptive list fields collapse
        // an empty list to null (AC-136).
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
        // MayDelegateFurther — and is refused, with the available set named, rather than silently honoured or
        // dropped. The task then starts with exactly the requested (validated) subset, applied in _ToolsForAsync.
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

        // A sub-agent inherits the project of the session that delegated to it (AC-320): it is doing a piece of that
        // session's work, so the servers, overrides and contributions its caller starts with are the ones it needs
        // too. Resolved here, once, and carried on the entry — the start path is where looking it up would cost the
        // UI thread. Absent resolver (a test graph, a caller off the verified path) leaves it null, which is exactly
        // how delegation behaved before.
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
    // is deliberately kept alive so the caller can follow up — so "finished" must not be read as "cannot take
    // another turn". A task whose session really is gone (stopped, or never started) is refused loudly rather
    // than accepted into the void, since a follow-up that silently does nothing is worse than an error: the
    // caller waits for a turn that will never come.
    public async Task<DelegatedTaskView> SendFollowUpAsync(string taskId, string text, string? callerPaneId = null, CancellationToken cancellationToken = default)
    {
        var entry = _Find(taskId, callerPaneId)
            ?? throw new DelegationRejectedException($"No task '{taskId}'.");

        if (entry.Runtime is not { IsRunning: true })
        {
            throw new DelegationRejectedException(
                $"Task '{taskId}' has no live session to continue (it is {entry.Status}). Delegate a new task instead.");
        }

        // The concurrency cap counts work being done on a profile, not just tasks being started on it: a
        // follow-up puts that session back to work, so it has to pass the same gate. It used to skip it, which
        // let a follow-up run alongside another task on a profile set to one at a time — exactly the parallel
        // load (a second model on the same GPU, a second draw on the same usage pot) the cap exists to prevent.
        // Refused rather than queued: a follow-up is the next turn of a conversation, and quietly deferring it
        // while the caller believes it is under way is the kind of silent lie this engine does not tell.
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

    // AC-128: an agent may delegate into the directory ITS OWN session is working in — not any directory some other
    // open session happens to be in. The old union let a pane confined to /repoX place a sub-agent in /repoY merely
    // because an unrelated pane was open there. Off the verified path (operator/in-process/tests) there is no single
    // caller, so the whole active set stands (the operator delegating on their own behalf).
    private IReadOnlyList<string> _CallerWorkspace(string? callerPaneId)
    {
        if (callerPaneId is null)
        {
            return _workspaces.ActiveWorkingDirectories;
        }

        // A UI pane's directory comes from the open-sessions provider. A delegated (headless) caller has no UI tab —
        // its verified pane id is its own task id — so fall back to that task's own working directory. Without this,
        // multi-level delegation (a MayDelegateFurther sub-agent delegating further into the directory it is itself
        // working in) is refused, because the pane lookup finds no UI session (AC-128 review follow-up).
        if (_workspaces.WorkingDirectoryForPane(callerPaneId) is { Length: > 0 } paneDirectory)
        {
            return [paneDirectory];
        }

        return _Find(callerPaneId)?.WorkingDirectory is { Length: > 0 } taskDirectory ? [taskDirectory] : [];
    }

    // Where a delegated task may run: the directories the target profile allows, and the ones the cockpit's own
    // sessions are already working in. The second is what makes delegation usable at all — you delegate *from*
    // a session in a repository, and that session can already read and write there, so the sub-agent it starts
    // reaches nothing its caller did not have. Everywhere else still needs the profile's own say-so.
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

        // AC-575/AC-89: from here on this flow acts for *this task's* owner, whoever happened to trigger the start.
        // McpRequestContext is an AsyncLocal identifying the request that entered the process, and it is inherited by
        // everything that request awaits — so the queue drainer (_StartNextQueuedAsync, reached inline from StopAsync,
        // from a timeout, and from a task's own completion event) used to run another owner's start under the
        // stopper's identity. The consent broker reads that ambient id for both the AC-575 bypass and the remember
        // key, so a task queued by pane X could be started above its ceiling on the strength of the assistant's
        // bypass, with the audit line written in the assistant's name and no card ever shown.
        //
        // Restamping rather than clearing, and no weakening of AC-89: OwnerPaneId was itself stamped from
        // McpRequestContext at delegate time (see DelegateAsync/OrchestratorTools), so it is the transport-verified
        // identity of the session that asked for this task and never anything a caller declared. It is null for a
        // task delegated off the verified path, which then reaches the broker as "no verified identity" — the
        // fail-closed case, where nothing is ever bypassed.
        //
        // Scoped to this method by construction: an async method's builder saves and restores the ExecutionContext
        // around its synchronous run, so this assignment flows down into the start (and the consent call inside it)
        // and never back out to the caller that triggered the drain. _StartAsync is the single chokepoint every
        // start goes through, so every caller is covered at once.
        McpRequestContext.Set(entry.OwnerPaneId);

        try
        {
            // A delegated session has no human to answer a permission prompt itself, so it runs under the profile's
            // ceiling by default — never bypass, never a mode that would block waiting for a click that cannot come.
            // A caller may cap this one task lower still, always honoured outright (AC-117). A request ABOVE the
            // ceiling is no longer silently clamped away when someone IS there to ask: see _EffectiveCeilingAsync.
            // Resolved before anything is created or marked running: the wait for an operator's answer is unbounded
            // from this method's own view, and a task sitting at Running with no session yet would occupy this
            // profile's concurrency slot and confuse a follow-up sent while nobody has answered yet.
            var effectiveCeiling = await _EffectiveCeilingAsync(entry);

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
                // AC-128/AC-89: give the delegated session its own verified MCP identity, keyed on the task id, so the
                // driver mints it a per-session SessionMcpKeyring token instead of the shared app key. Without this a
                // sub-agent's own orchestrator calls arrive as a null — unscoped — caller and could reach every
                // session's tasks: the confused deputy the owner-scoping closes, reopened for the one actor that runs
                // agent-driven end to end (a MayDelegateFurther sub-agent).
                launchOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [WellKnownPluginSessionOptions.PaneId] = entry.TaskId,
                    // AC-378: nobody is watching a delegated task, so its narrowing must be authoritative — the driver
                    // hands the CLI exactly the servers _ToolsForAsync resolved and nothing from the operator's own
                    // user/project config. This is the path where the escalation was measured: asking for a server that
                    // resolves to nothing used to yield a session holding every account connector instead of none.
                    [WellKnownPluginSessionOptions.Unattended] = "true",
                },
                // The project this task inherited from the session that delegated it (AC-320), so its MCP fan-out
                // resolves the registry as that project sees it (AC-218) rather than unscoped. A value, never a
                // lookup: this runs where the driver may resolve synchronously.
                projectId: entry.ProjectId);

            // The ceiling above governs a CLI session's own permission handling, but a local-model session
            // (OpenAiCompatSessionDriver) treats permissionMode as a no-op and gates every MCP tool call through
            // the interactive PermissionRequested flow. With no human to answer it, the task would hang on its
            // first tool call until the timeout — the "block waiting for a click that cannot come" the ceiling is
            // meant to prevent (AC-78). A delegated session is non-interactive by definition, so it must decide
            // tool calls itself, never prompt. Two ways, by what the operator chose for the profile:
            //   - "Auto-Approve tool calls" on → the operator trusts this profile fully, so allow everything
            //     (still bounded by the policy-restricted enabled-server set).
            //   - otherwise → gate each tool call against the ceiling + the profile's tool allow-list (AC-79):
            //     read-only runs, a write runs only at acceptEdits/bypass, a destructive only at bypass, and an
            //     unclassifiable tool runs only if allow-listed — anything else is denied with a reason, not hung.
            // Harmless for a CLI driver: both are default no-ops there, since it gates through its own CLI mode.
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

    // The permission ceiling this task's session actually runs under (AC-117). A per-task
    // `DelegatedTaskEntry.RequestedPermission` that asks for no more than the profile's own
    // `DelegationPolicy.PermissionCeiling` is honoured outright — narrowing what the operator already
    // allowed needs nobody's further say-so.
    //
    // A request ABOVE the ceiling used to be clamped away with nobody the wiser. Now, with the operator's
    // Approve/Deny gate attached (#AC-47), it is put to them instead: a one-time consent to run this profile above
    // its configured ceiling for this one task. Classed `ConsentRisk.Dangerous` and never remembered —
    // this is exactly the "starting or steering a session with the operator's rights" case that risk class exists
    // for, and a delegated agent can be prompt-injected into asking for it, so one approval must never become a
    // standing permission it (or a later task on the same profile) can ride again. Denied, or with no gate to ask
    // (a headless delegation chain, or no UI open), it falls back to the clamp: the profile's ceiling wins
    // whenever nobody was there to say otherwise. Bounded by the profile's own `DelegationPolicy.TimeoutMinutes`
    // so an unanswered prompt cannot hold this task's slot, and this pane's whole consent channel, open forever.
    private async Task<string> _EffectiveCeilingAsync(DelegatedTaskEntry entry)
    {
        var requested = entry.RequestedPermission;
        var profileCeiling = entry.Profile.DelegationPolicy.PermissionCeiling;
        if (string.IsNullOrWhiteSpace(requested))
        {
            return profileCeiling;
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

    // Closes a finished task's session once nobody has followed up on it for `IdleSessionWindow`. Without
    // this a delegated session lived until the app did: an orchestrator that has its answer has no reason to call
    // stop_task, and every task it ever ran would still be holding a process — or a model in a local server's
    // memory. The result is kept; only the session and the worktree it worked in go.
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
    // call and so the same cleanup policy the cockpit applies when the operator closes a pane
    // (`CloseSessionAsync`): a clean checkout is removed, taking its branch with it when that work is already
    // in the base branch, and one that still holds work is kept and marked retained for review. A delegated task's
    // verified pane id is its task id, which is what its `worktree_create` calls were keyed on, so that id is
    // all the manager needs.
    //
    // Called from every path that ends a delegated session for good: stop, idle reap, the profile's timeout, and a
    // task that never got as far as running. The first three have torn the session down before they get here; the
    // fourth never started one, so it can have no worktree — the call is a no-op there, made anyway so the rule has
    // no exception to remember. A driver error is the one ending that is *not* in this list, and the reason
    // is at that call site: it does not mean the session is over.
    //
    // Claimed rather than merely called, because two closing paths can land together (see
    // `DelegatedTaskEntry.TryClaimWorktreeRelease`). Best-effort as the pane teardown is: a worktree git
    // will not let go of must not turn into a failed stop_task or a timeout that never reports, and whatever is left
    // behind is what the reconcile is for.
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

    // Stops a task that outlives what its profile allows. Nobody is watching a delegated session, so a model that
    // loops or waits on something that never comes would otherwise hold the profile's slot — and keep drawing on
    // its provider — until the app closes. The timer is cancelled the moment the task ends, so a finished task is
    // never stopped after the fact.
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

    // The MCP servers a delegated session gets, loading the live registry and applying `_NarrowServersFor`.
    // A sub-agent still needs its files, its shell, its git, so the default is everything the operator enabled —
    // narrowed by the profile's own pre-selection and the caller's per-task selection, and minus the orchestrator
    // unless the profile may delegate further. Withholding the orchestrator is the second lock on the recursion
    // guard: even if the depth check in `_Guard` were wrong, a sub-agent with no delegate_task tool
    // cannot start a chain.
    internal async Task<IReadOnlySet<string>> _ToolsForAsync(SessionProfile profile, IReadOnlyList<string>? perTaskSelection = null)
    {
        var registry = await _mcpServerStore.LoadAsync();
        return _NarrowServersFor(registry, profile, perTaskSelection);
    }

    // The pure narrowing behind `_ToolsForAsync`: the enabled registry servers, intersected with the
    // profile's saved pre-selection (AC-133/AC-130) and then the caller's per-task selection (AC-136) when each is
    // set, minus the orchestrator unless the profile may delegate further. Both intersections only ever remove — a
    // name in neither the selection nor the enabled registry cannot appear — so a delegated session can be narrowed
    // but never widened past what the operator enabled. A null selection means "no restriction at that layer".
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

                // False-success guard (AC-100/AC-110): the local-model driver reports a turn as "success" whenever
                // the HTTP stream ends cleanly — even when every tool call it made was denied or errored and it
                // produced nothing. A turn that ran tools but landed none of them is not a success; surface it as
                // Failed with a diagnostic so a no-op run is never silently relayed as done. A turn that used no
                // tools at all (a plain text answer) is left as Completed — that is a legitimate result.
                var ranToolsButNoneSucceeded = entry.ToolCallsRequested > 0 && entry.ToolCallsSucceeded == 0;
                var isFailure = turn.IsError || ranToolsButNoneSucceeded;
                var diagnostic = turn.IsError
                    ? turn.Result
                    : ranToolsButNoneSucceeded
                        ? $"No-op run: {entry.ToolCallsErrored} of {entry.ToolCallsRequested} tool call(s) were blocked or errored and none succeeded, so the task produced no tool-made change. The delegated model replied: {entry.Runtime?.LastAssistantText}"
                        : null;

                // Per-turn, not per-session: clear the counters now this turn is classified, so a follow-up turn
                // (SendFollowUpAsync reuses the same entry) is judged on its own tool calls. Without this a plain
                // text follow-up after a denied turn would inherit that denial (false failure), and a denied
                // follow-up after a successful turn would be hidden as success (false success) — AC-100 review.
                entry.ToolCallsRequested = 0;
                entry.ToolCallsSucceeded = 0;
                entry.ToolCallsErrored = 0;

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
                break;

            // Deliberately no worktree release here, unlike every other path that ends a task. A SessionError is not
            // proof that the session is over: every PluginSessionError becomes one (PluginSessionDriverAdapter), and
            // some are notices from a session that is running perfectly well — the cockpit falling behind on events
            // (PluginSessionEventPublisher's gap notice), or a driver saying it could not apply a system prompt.
            // Handing the checkout back on one of those would delete a live sub-agent's working directory, since a
            // momentarily clean worktree is removed outright. So this one stays with the startup reconcile.
            case SessionError error:
                entry.Finish(DelegatedTaskStatus.Failed, result: null, error: error.Message);
                TasksChanged?.Invoke();
                _ = _Audit(DelegationAuditAction.Failed, entry.Profile.Label, entry.TaskId, request: null, error.Message, entry);
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
