using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Projects;

// AC-1435: the one writer of the projects section, for the Projects page and for a caller with no window alike, and
// the read model that page draws. Moved here from ProjectsViewModel: the shared-project claims, the visibility filter,
// the Depot sync state and the logo copy. Writes are serialised, so two adds at once both land (AC-799).
internal sealed class ProjectCatalog : IProjectCatalog, IProjectEditor, ISingletonService
{
    private readonly IProjectStore _store;
    private readonly IProjectLogoStore? _logos;
    private readonly IProjectOwnershipRegistry _ownership;
    private readonly ISharedProjectSourceRegistry _sharedSources;
    private readonly DepotSyncWatcher? _depotSync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _remoteChangedProjectIds = new(StringComparer.Ordinal);

    private ProjectSettings _settings = ProjectSettings.Empty;
    private bool _loaded;
    private IReadOnlyList<SharedProjectGroup> _sharedProjectGroups = [];
    private ProjectCatalogSnapshot _current = ProjectCatalogSnapshot.Empty;

    // Cancels a shared-project read still running when a newer one starts, so a slow connection cannot overwrite a
    // fresher answer with a stale one.
    private CancellationTokenSource? _sharedProjectsLoadCts;

    public ProjectCatalog(
        IProjectStore store,
        IProjectOwnershipRegistry ownership,
        ISharedProjectSourceRegistry sharedSources,
        IProjectLogoStore? logos = null,
        DepotSyncWatcher? depotSync = null)
    {
        _store = store;
        _ownership = ownership;
        _sharedSources = sharedSources;
        _logos = logos;
        _depotSync = depotSync;

        // AC-762: a source that registers after the first read (plugin phase 2 runs after the window's first load)
        // gets its own read instead of leaving every card on it stuck until the operator opens Manage projects.
        _sharedSources.Registered += source => { _ = RefreshSharedProjectsAsync(); };

        // AC-894: the watcher asks which projects to poll every tick and reports each check back here.
        if (_depotSync is not null)
        {
            _depotSync.BoundProjects = DepotBoundProjects;
            _depotSync.OnChecked = SetRemoteChangeStateAsync;
        }
    }

    public ProjectCatalogSnapshot Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            _loaded = true;
            _Publish();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke();
    }

    public async Task<Project?> FindProjectAsync(string projectId) =>
        (await _ReadSettingsAsync().ConfigureAwait(false)).Projects.FirstOrDefault(project => project.Id == projectId);

    public Task<Project> AddNewProjectAsync(Project project) =>
        _WriteAsync(async settings =>
        {
            var stored = await _WithStoredLogoAsync(project).ConfigureAwait(false);
            return (settings.WithProject(stored), stored);
        });

    // The row just bound must not stay on offer until the next full reload: a fresh read finds it bound now.
    public async Task<Project> AddBoundProjectAsync(Project project)
    {
        var stored = await AddNewProjectAsync(project).ConfigureAwait(false);
        await RefreshSharedProjectsAsync().ConfigureAwait(false);
        return stored;
    }

    public Task<Project?> UpdateStoredProjectAsync(Project project) =>
        _WriteAsync<Project?>(async settings =>
        {
            if (settings.Projects.All(candidate => candidate.Id != project.Id))
            {
                return (null, null);
            }

            var stored = await _WithStoredLogoAsync(project).ConfigureAwait(false);
            return (settings.WithUpdated(stored), stored);
        });

    // A project removed in the meantime is left alone rather than written back; AC-490 records a job run only on true.
    public Task<bool> MarkOpenedAsync(string projectId, DateTimeOffset openedAt) =>
        _WriteAsync(settings => Task.FromResult(
            settings.Projects.FirstOrDefault(candidate => candidate.Id == projectId) is { } stored
                ? (settings.WithUpdated(stored with { LastOpenedAt = openedAt }), true)
                : ((ProjectSettings?)null, false)));

    public Task RemoveProjectAsync(string projectId) =>
        _WriteAsync(settings =>
        {
            _logos?.Remove(projectId);
            return Task.FromResult<(ProjectSettings?, bool)>((settings.WithoutProject(projectId), true));
        });

    public Task RefreshSharedProjectsAsync(CancellationToken cancellationToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _sharedProjectsLoadCts, cts)?.Cancel();
        return _LoadSharedProjectsAsync(cts.Token);
    }

    // AC-1375: the assistant's bind and create read the sources and the ids already bound or hidden as one instant.
    internal async Task<(IReadOnlyList<ISharedProjectSource> Sources, HashSet<string> BoundIds, HashSet<string> HiddenIds)> ReadSharedProjectSourcesAsync()
    {
        var (bound, hidden) = SharedProjectSourceLister.VisibilityFilterIds(await _ReadSettingsAsync().ConfigureAwait(false));
        return (_sharedSources.Sources, bound, hidden);
    }

    public Task SyncNowAsync(string projectId) => _depotSync?.SyncNowAsync(projectId) ?? Task.CompletedTask;

    // AC-894: every local project genuinely bound to a source right now, and the id the watcher should ask it about.
    internal IReadOnlyList<DepotBoundProject> DepotBoundProjects()
    {
        var bound = new List<DepotBoundProject>();
        foreach (var project in Volatile.Read(ref _settings).Projects)
        {
            var sharedId = _BoundTo(project);
            if (sharedId is { Length: > 0 } && _ResolveSharedSource(project) is { } source)
            {
                bound.Add(new DepotBoundProject(project.Id, source, sharedId));
            }
        }

        return bound;
    }

    // AC-894: the watcher's report for one project. AC-1054: the logo bytes that check re-downloaded are adopted only
    // into a project with no logo of its own yet, so a sync check never overwrites a local choice.
    internal async Task SetRemoteChangeStateAsync(string projectId, bool hasRemoteChange, byte[]? logoBytes)
    {
        bool moved;
        lock (_remoteChangedProjectIds)
        {
            moved = hasRemoteChange ? _remoteChangedProjectIds.Add(projectId) : _remoteChangedProjectIds.Remove(projectId);
        }

        if (moved)
        {
            _PublishAndRaise();
        }

        if (_logos is null || logoBytes is not { Length: > 0 })
        {
            return;
        }

        await _WriteAsync<bool>(async settings =>
        {
            if (settings.Projects.FirstOrDefault(project => project.Id == projectId) is not { LogoPath: null } project
                || TempLogoFile.WriteOrNull(logoBytes) is not { } tempPath)
            {
                return (null, false);
            }

            var stored = await _WithStoredLogoAsync(project with { LogoPath = tempPath }).ConfigureAwait(false);
            return (settings.WithUpdated(stored), true);
        }).ConfigureAwait(false);
    }

    private async Task _LoadSharedProjectsAsync(CancellationToken cancellationToken)
    {
        var sources = _sharedSources.Sources;
        if (sources.Count == 0)
        {
            _sharedProjectGroups = [];
            _PublishAndRaise();
            return;
        }

        var (boundIds, hiddenIds) = SharedProjectSourceLister.VisibilityFilterIds(await _ReadSettingsAsync().ConfigureAwait(false));
        var results = await Task.WhenAll(sources.Select(source => SharedProjectSourceLister.ListWithTimeoutAsync(source, cancellationToken)))
            .ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Claiming runs over every project a source reported, bound or not: a bound one is exactly what the claim is
        // for, even though it never shows in that source's own group.
        await _WriteAsync<bool>(settings =>
        {
            var reconciled = settings;
            foreach (var (source, result) in sources.Zip(results).Where(pair => pair.Second.Succeeded))
            {
                foreach (var project in _ReconcileSharedSourceClaims(settings, result.Projects, source))
                {
                    reconciled = reconciled.WithUpdated(project);
                }
            }

            return Task.FromResult<(ProjectSettings?, bool)>((ReferenceEquals(reconciled, settings) ? null : reconciled, true));
        }).ConfigureAwait(false);

        var groups = new List<SharedProjectGroup>();
        foreach (var (source, result) in sources.Zip(results))
        {
            var visible = result.Succeeded
                ? result.Projects
                    .Where(project => !boundIds.Contains(project.Id) && !hiddenIds.Contains(project.Id))
                    .Select(project => new SharedProjectOffer(project.Id, project.Name, project.Description, project.Role))
                    .ToList()
                : [];

            if (visible.Count == 0 && result.Succeeded)
            {
                continue;
            }

            groups.Add(new SharedProjectGroup(source.SourceName, visible, result.Succeeded ? null : result.Error));
        }

        _sharedProjectGroups = groups;
        _PublishAndRaise();
    }

    // Every local project bound to `sharedProjects` is claimed as owned by `source`; returns the projects whose
    // `SharedSourceName` needs persisting to match (AC-762): confirmed when still listed, cleared only when a
    // project's claim names this exact source but its successful list no longer contains it.
    private List<Project> _ReconcileSharedSourceClaims(ProjectSettings settings, IReadOnlyList<SharedProject> sharedProjects, ISharedProjectSource source)
    {
        var byId = sharedProjects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var updated = new List<Project>();

        foreach (var project in settings.Projects)
        {
            if (_BoundTo(project) is not { Length: > 0 } boundTo)
            {
                continue;
            }

            if (byId.TryGetValue(boundTo, out var sharedProject))
            {
                // AC-247/AC-763: every claimed field unlocks once the source says this role can write back.
                _ownership.Register(new ProjectOwnershipRegistration(
                    project.Id, new ProjectFieldOwnership(source.SourceName, IsEditable: sharedProject.CanWriteBack, Role: sharedProject.Role)));

                if (!string.Equals(project.SharedSourceName, source.SourceName, StringComparison.Ordinal))
                {
                    updated.Add(project with { SharedSourceName = source.SourceName });
                }
            }
            else if (string.Equals(project.SharedSourceName, source.SourceName, StringComparison.Ordinal))
            {
                updated.Add(project with { SharedSourceName = null });
            }
        }

        return updated;
    }

    // The source `project` is genuinely bound to: a matching Memory-reference prefix alone is not enough (AC-744). A
    // live claim or, absent that (AC-762), the persisted `SharedSourceName` makes it claimed.
    private ISharedProjectSource? _ResolveSharedSource(Project project)
    {
        var isClaimed = _ownership.Resolve(project.Id) is not null || project.SharedSourceName is { Length: > 0 };
        return isClaimed && _BoundTo(project) is { Length: > 0 } boundTo
            ? _sharedSources.Sources.FirstOrDefault(source => boundTo.StartsWith(source.Key + ":", StringComparison.Ordinal))
            : null;
    }

    private static string? _BoundTo(Project project) =>
        project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;

    // `project` with its logo as a copy the cockpit owns.
    private async Task<Project> _WithStoredLogoAsync(Project project)
    {
        if (_logos is null)
        {
            return project;
        }

        if (project.LogoPath is not { Length: > 0 } source)
        {
            _logos.Remove(project.Id);
            return project with { LogoPath = null };
        }

        // Already the copy: re-storing it would read the file the cockpit is about to overwrite.
        if (_logos.IsStoredCopy(source))
        {
            return project;
        }

        return project with { LogoPath = await _logos.SaveAsync(project.Id, source).ConfigureAwait(false) };
    }

    private async Task<ProjectSettings> _ReadSettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _EnsureLoadedAsync().ConfigureAwait(false);
            return _settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    // `change` returns the settings to store, or null to store nothing, plus the caller's answer.
    private async Task<T> _WriteAsync<T>(Func<ProjectSettings, Task<(ProjectSettings? Next, T Result)>> change)
    {
        T result;
        bool changed;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _EnsureLoadedAsync().ConfigureAwait(false);
            (var next, result) = await change(_settings).ConfigureAwait(false);
            changed = next is not null;
            if (next is not null)
            {
                _settings = next;
                await _store.SaveAsync(next).ConfigureAwait(false);
                _Publish();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return result;
    }

    private async Task _EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _settings = await _store.LoadAsync().ConfigureAwait(false);
            _loaded = true;
            _Publish();
        }
    }

    private void _PublishAndRaise()
    {
        _Publish();
        Changed?.Invoke();
    }

    private void _Publish()
    {
        var settings = Volatile.Read(ref _settings);
        var claims = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var project in settings.Projects)
        {
            if (_ownership.Resolve(project.Id) is { } resolved)
            {
                claims[project.Id] = resolved.Values.FirstOrDefault(ownership => ownership is not null)?.SourceName;
            }
        }

        HashSet<string> remoteChanged;
        lock (_remoteChangedProjectIds)
        {
            remoteChanged = new HashSet<string>(_remoteChangedProjectIds, StringComparer.Ordinal);
        }

        Volatile.Write(ref _current, new ProjectCatalogSnapshot(
            settings,
            [.. _sharedSources.Sources.Select(source => new SharedProjectSourceInfo(source.Key, source.SourceName, source.CanPublish))],
            _sharedProjectGroups,
            claims,
            remoteChanged));
    }
}
