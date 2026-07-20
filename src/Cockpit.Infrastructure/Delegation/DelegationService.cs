using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

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

    /// <summary>
    /// How long a finished task's session is kept alive for a follow-up before the cockpit closes it. The session is
    /// deliberately not torn down the moment a turn ends — the caller may want to ask one more thing, and a
    /// conversation that has to be started again is not a conversation. But an orchestrator that simply never calls
    /// stop_task (the common case: it has its answer and moves on) would leave a sub-agent sitting there until the
    /// app closes — a CLI process, or an Ollama model held in memory, doing nothing at all. So it reaps itself, and
    /// a follow-up within the window puts it back to work and starts the clock again.
    /// </summary>
    private static readonly TimeSpan IdleSessionWindow = TimeSpan.FromMinutes(5);

    private readonly ISessionProfileStore _profileStore;
    private readonly ISessionManager _sessionManager;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly IDelegationAuditLog _auditLog;
    private readonly ISessionWorkspaces _workspaces;
    private readonly IPluginProviderRegistry? _providerRegistry;
    private readonly Func<int, TimeSpan> _timeout;
    private readonly List<DelegatedTaskEntry> _tasks = [];
    private readonly Lock _tasksLock = new();

    public DelegationService(
        ISessionProfileStore profileStore,
        ISessionManager sessionManager,
        IMcpServerStore mcpServerStore,
        IDelegationAuditLog auditLog,
        ISessionWorkspaces workspaces,
        IPluginProviderRegistry? providerRegistry = null)
        : this(profileStore, sessionManager, mcpServerStore, auditLog, minutes => TimeSpan.FromMinutes(minutes), workspaces, providerRegistry)
    {
    }

    /// <summary>Test seam: lets a test express the profile's timeout in milliseconds rather than waiting minutes for it.</summary>
    internal DelegationService(
        ISessionProfileStore profileStore,
        ISessionManager sessionManager,
        IMcpServerStore mcpServerStore,
        IDelegationAuditLog auditLog,
        Func<int, TimeSpan> timeout,
        ISessionWorkspaces? workspaces = null,
        IPluginProviderRegistry? providerRegistry = null)
    {
        _profileStore = profileStore;
        _sessionManager = sessionManager;
        _mcpServerStore = mcpServerStore;
        _auditLog = auditLog;
        _workspaces = workspaces ?? NoSessionWorkspaces.Instance;
        _providerRegistry = providerRegistry;
        _timeout = timeout;
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

    /// <summary>
    /// Writes back what a profile turned out to be good for — and nothing else. Only the three descriptive fields are
    /// touched; the rest of the policy is rebuilt from what the operator set, so a caller cannot make itself a target,
    /// raise a ceiling, or open a directory by calling this. A profile that is not already a target is refused, for
    /// the same reason: it is not a caller's to enrol.
    /// </summary>
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
        saved[index] = profile with { Delegation = updated };
        await _profileStore.SaveAsync(saved, cancellationToken);

        return new DelegationTargetView(
            profile.Label,
            profile.Provider.ToString(),
            updated.Purpose,
            updated.Tags ?? [],
            updated.AllowedTaskTypes ?? [],
            updated.MaxConcurrent,
            _CountRunning(profile.Label));
    }

    /// <summary>The base URL a local provider defaults to when the caller does not give one.</summary>
    private const string OllamaDefaultBaseUrl = "http://localhost:11434";
    private const string LmStudioDefaultBaseUrl = "http://localhost:1234";

    /// <summary>
    /// Adds a local-model profile and saves it — but never as a delegation target. The soft purpose/tags a caller
    /// suggests are carried, so the operator's later opt-in starts from them; the hard policy stays default and off
    /// (<see cref="DelegationPolicy.AllowedAsTarget"/> false), because enrolling a target and setting its ceiling is
    /// the operator's call. Local only: an Ollama or LM Studio model runs here and carries no login, so scaffolding
    /// one cannot leak a credential or spend a subscription — a Claude profile is the operator's to make.
    /// </summary>
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

    /// <summary>
    /// Every provider a session can run under: the two local ones a caller may scaffold with
    /// <see cref="AddLocalModelProfileAsync"/>, then each provider a plugin registered — the operator's to set up,
    /// since such a provider may carry a login. So a caller can discover what exists — and which of it is theirs to
    /// add — instead of guessing provider names or finding out only when add_profile refuses.
    /// </summary>
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

        var entry = new DelegatedTaskEntry(profile, request) { OwnerPaneId = callerPaneId };
        lock (_tasksLock)
        {
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

    /// <summary>
    /// Continues a task with another turn. A task that has answered is <em>Completed</em>, not gone: its session
    /// is deliberately kept alive so the caller can follow up — so "finished" must not be read as "cannot take
    /// another turn". A task whose session really is gone (stopped, or never started) is refused loudly rather
    /// than accepted into the void, since a follow-up that silently does nothing is worse than an error: the
    /// caller waits for a turn that will never come.
    /// </summary>
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

    /// <summary>The directories a delegated task may run in: the profile's own allow-list plus the dir the calling session is itself working in (AC-128 — scoped to the caller, not every open session). Surfaced in the rejection reason (AC-114) so a refused caller can see where it may go.</summary>
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

    /// <summary>
    /// Where a delegated task may run: the directories the target profile allows, and the ones the cockpit's own
    /// sessions are already working in. The second is what makes delegation usable at all — you delegate <em>from</em>
    /// a session in a repository, and that session can already read and write there, so the sub-agent it starts
    /// reaches nothing its caller did not have. Everywhere else still needs the profile's own say-so.
    /// </summary>
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
        try
        {
            var runtime = _sessionManager.Create(entry.Profile);
            entry.Attach(runtime);

            // Mark it running *before* the pump can deliver anything. A fast session can complete its turn while
            // this method is still unwinding, and setting Running afterwards would overwrite the Completed the
            // event handler had already recorded — leaving a finished task reported as still working.
            entry.Status = DelegatedTaskStatus.Running;
            entry.StartedAt = DateTimeOffset.Now;
            TasksChanged?.Invoke();

            runtime.EventAppended += evt => _OnTaskEvent(entry, evt);

            // A delegated session has no human to answer a permission prompt, so it runs under the profile's
            // ceiling — never bypass, never a mode that would block waiting for a click that cannot come. A caller
            // may cap this one task lower still (AC-117): the effective ceiling is the more restrictive of the two,
            // so a per-task request can only narrow what the operator allowed, never widen it.
            var effectiveCeiling = string.IsNullOrWhiteSpace(entry.RequestedPermission)
                ? entry.Profile.DelegationPolicy.PermissionCeiling
                : DelegatedToolPermissionPolicy.MoreRestrictiveCeiling(
                    entry.Profile.DelegationPolicy.PermissionCeiling, entry.RequestedPermission);

            await runtime.StartAsync(
                entry.Profile,
                effectiveCeiling,
                model: null,
                enabledMcpServerNames: await _ToolsForAsync(entry.Profile),
                workingDirectory: entry.WorkingDirectory,
                // AC-128/AC-89: give the delegated session its own verified MCP identity, keyed on the task id, so the
                // driver mints it a per-session SessionMcpKeyring token instead of the shared app key. Without this a
                // sub-agent's own orchestrator calls arrive as a null — unscoped — caller and could reach every
                // session's tasks: the confused deputy the owner-scoping closes, reopened for the one actor that runs
                // agent-driven end to end (a MayDelegateFurther sub-agent).
                launchOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [WellKnownPluginSessionOptions.PaneId] = entry.TaskId,
                });

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
            // A task that cannot start is a visibly failed task, not one that quietly sits at Queued forever.
            entry.Finish(DelegatedTaskStatus.Failed, result: null, error: ex.Message);
            TasksChanged?.Invoke();
            await _Audit(DelegationAuditAction.Failed, entry.Profile.Label, entry.TaskId, request: null, ex.Message, entry);
        }
    }

    /// <summary>
    /// Closes a finished task's session once nobody has followed up on it for <see cref="IdleSessionWindow"/>. Without
    /// this a delegated session lived until the app did: an orchestrator that has its answer has no reason to call
    /// stop_task, and every task it ever ran would still be holding a process — or a model in a local server's
    /// memory. The result is kept; only the session goes.
    /// </summary>
    private void _ArmIdleReap(DelegatedTaskEntry entry)
    {
        var idle = new CancellationTokenSource();
        entry.IdleCancellation = idle;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(IdleSessionWindow, idle.Token);
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
                TasksChanged?.Invoke();
            }
        });
    }

    /// <summary>
    /// Stops a task that outlives what its profile allows. Nobody is watching a delegated session, so a model that
    /// loops or waits on something that never comes would otherwise hold the profile's slot — and keep drawing on
    /// its provider — until the app closes. The timer is cancelled the moment the task ends, so a finished task is
    /// never stopped after the fact.
    /// </summary>
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

    /// <summary>
    /// The MCP servers a delegated session gets: everything the operator enabled — a sub-agent still needs its
    /// files, its shell, its git — minus the orchestrator itself, unless the profile explicitly allows delegating
    /// further. Withholding the tools is the second lock on the recursion guard: even if the depth check in
    /// <see cref="_Guard"/> were wrong, a sub-agent with no delegate_task tool cannot start a chain.
    /// </summary>
    private async Task<IReadOnlySet<string>> _ToolsForAsync(SessionProfile profile)
    {
        var registry = await _mcpServerStore.LoadAsync();
        var names = registry
            .Where(server => server.Enabled)
            .Select(server => server.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
