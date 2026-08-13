using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// The projects manager behind Options → Projects (AC-161): the saved projects, and add/edit/remove over them.
// Owns the persisting that `ProjectDialogViewModel` deliberately does not, so the editor stays a
// value editor and this is the only thing that writes the list.
//
// Since AC-245, also the Projects workspace's read side for what a plugin shares elsewhere but this machine has
// not bound yet — see `SharedProjectGroups` and `LoadSharedProjectsAsync`.
public partial class ProjectsViewModel : ViewModelBase, ISingletonService
{
    private readonly IProjectStore _store;

    // Takes the cockpit's own copy of a picked or downloaded logo. Null under the previewer, where a project keeps whatever path it was given.
    private readonly IProjectLogoStore? _logos;

    // Null only under the previewer, which has no window to open a dialog over; every command that needs one is inert there.
    private readonly ISessionDialogService? _dialogs;

    // Where a shared project's origin is claimed for a local project already bound to it (AC-604/AC-245's own
    // consumption of it — see `LoadSharedProjectsAsync`). Null under the previewer.
    private readonly IProjectOwnershipRegistry? _ownership;

    // What every registered plugin shares elsewhere (AC-245). Null under the previewer, where `SharedProjectGroups` simply stays empty.
    private readonly ISharedProjectSourceRegistry? _sharedSources;

    private ProjectSettings _settings = ProjectSettings.Empty;

    // Cancels a still-running `LoadSharedProjectsAsync` when a newer one starts (the workspace reopened, say), so a slow connection cannot overwrite a fresher answer with a stale one.
    private CancellationTokenSource? _sharedProjectsLoadCts;

    // The background `LoadSharedProjectsAsync` call `LoadAsync` most recently started
    // (never awaited by it — see that method's own remarks). Internal test seam only: it lets a test await the
    // same run `LoadAsync` kicked off instead of racing it with a second, independent call.
    internal Task SharedProjectsLoadTask { get; private set; } = Task.CompletedTask;

    // Design-time constructor for the Avalonia previewer: an empty store and no dialog service, so a rendered
    // surface can reach neither the operator's config nor a window that does not exist there. The commands are
    // inert in that context — see `_dialogs`.
    public ProjectsViewModel()
        : this(new DesignTimeProjectStore(), dialogs: null)
    {
    }

    public ProjectsViewModel(
        IProjectStore store,
        ISessionDialogService? dialogs,
        IProjectLogoStore? logos = null,
        IProjectOwnershipRegistry? ownership = null,
        ISharedProjectSourceRegistry? sharedSources = null)
    {
        _store = store;
        _dialogs = dialogs;
        _logos = logos;
        _ownership = ownership;
        _sharedSources = sharedSources;

        // AC-762: a source that registers after the startup race already lost it (App.axaml.cs's plugin phase 2
        // runs after CockpitViewModel's constructor kicks off the first LoadAsync) gets its own retry instead of
        // leaving every card on that source stuck until the operator happens to open Manage projects.
        if (_sharedSources is not null)
        {
            _sharedSources.Registered += _OnSharedSourceRegistered;
        }
    }

    private void _OnSharedSourceRegistered(ISharedProjectSource source) => _BeginSharedProjectsLoad();

    // Cancels a still-running load and starts a fresh one — the same dance `LoadAsync` does after re-reading
    // settings, factored out so a source registering later (`_OnSharedSourceRegistered`) can trigger the same
    // retry without re-reading `_settings` from disk for no reason.
    private void _BeginSharedProjectsLoad()
    {
        _sharedProjectsLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sharedProjectsLoadCts = cts;
        SharedProjectsLoadTask = LoadSharedProjectsAsync(cts.Token);
    }

    // The saved projects in the order they are stored — what the manager lists and edits.
    public ObservableCollection<Project> Projects { get; } = [];

    // The same projects, most recently opened first and never-opened ones after them by name — what the overview
    // leads with. A separate list rather than a re-sorted `Projects`: the manager's order is the
    // operator's own, and re-ordering it under them every time a session starts would be its own small chaos.
    public ObservableCollection<Project> RecentProjects { get; } = [];

    // The few most recently worked on, for the sidebar (Raymond, 2026-07-24): that strip is for reaching what you
    // are busy with, and a list that grows with every project turns it back into a menu. The rest stay one click
    // away in the overview.
    public ObservableCollection<Project> SidebarProjects { get; } = [];

    // How many of them the sidebar shows.
    private const int SidebarLimit = 5;

    // How long `LoadSharedProjectsAsync` waits on one source before treating it as failed — one slow
    // or hung connection must not hold up every other source's rows, let alone the whole workspace. Internal and
    // mutable (rather than a private constant) purely as a test seam: a real 10s wait has no place in a unit test
    // that wants to prove the timeout path itself.
    internal static TimeSpan SharedProjectSourceTimeout = TimeSpan.FromSeconds(10);

    // Shared projects (AC-245), one group per registered `ISharedProjectSource` — "Shared via Depot —
    // Work" — already bound projects and this machine's own hidden ones filtered out. Empty until
    // `LoadSharedProjectsAsync` has run at least once; `LoadAsync` starts it in the
    // background rather than waiting on it, so opening the workspace never blocks on a slow or unreachable
    // connection — see that method's own remarks.
    public ObservableCollection<SharedProjectGroupViewModel> SharedProjectGroups { get; } = [];

    // Whether there is anything to show under a "Shared" heading right now — lets the workspace leave the whole section out rather than draw an empty one.
    public bool HasSharedProjects => SharedProjectGroups.Count > 0;

    // AC-248: gates the launcher's own pointer line separately from HasSharedProjects, so it never contradicts a
    // signed-out connection's own "Sign in to this Depot connection…" error by implying nothing is set up.
    public bool HasNoSharedProjectSources => _sharedSources is null || _sharedSources.Sources.Count == 0;

    // `Projects` grouped by category for the list (AC-618), rebuilt by `_Republish` —
    // replaces AC-245's "On this machine" heading with a per-card origin badge instead
    // (`ProjectCardViewModel.OriginBadge`). No project with a category anywhere means exactly one
    // group with a null `ProjectCategoryGroupViewModel.CategoryName`, which the workspace draws with
    // no heading at all — see that type's own remarks.
    public ObservableCollection<ProjectCategoryGroupViewModel> ProjectCategoryGroups { get; } = [];

    // The always-present, never-disappearing catch-all category group's heading (AC-618).
    private const string _UncategorizedLabel = "Uncategorized";

    // Whether the workspace has nothing at all to show — what the "No projects yet" empty state is gated on
    // instead of `!HasProjects` alone, so that text does not sit above a populated "Shared via …" section
    // once one arrives a moment after the window opens (`LoadSharedProjectsAsync` runs in the background).
    public bool HasNothingToShow => !HasProjects && !HasSharedProjects;

    // True when there are more projects than the sidebar shows, so it can say where the others are.
    public bool HasMoreThanSidebarShows => Projects.Count > SidebarLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Project? _selectedProject;

    public bool HasSelection => SelectedProject is not null;

    public bool HasProjects => Projects.Count > 0;

    // How many projects there are, for the overview's summary line.
    public int ProjectCount => Projects.Count;

    // How many have ever been opened — the rest are set up but never started, which is worth seeing at a glance.
    public int OpenedProjectCount => Projects.Count(project => project.LastOpenedAt is not null);

    // The project a session was last started on, or null when none ever was.
    public Project? MostRecentProject => RecentProjects.FirstOrDefault(project => project.LastOpenedAt is not null);

    // Records that a session just started on `project`, so the overview can lead with what is
    // actually worked on. Persists like every other change here; a project removed in the meantime is left alone
    // rather than written back.
    public async Task MarkOpenedAsync(Project project, DateTimeOffset openedAt)
    {
        if (_settings.Projects.FirstOrDefault(candidate => candidate.Id == project.Id) is not { } stored)
        {
            return;
        }

        await _PersistAsync(_settings.WithUpdated(stored with { LastOpenedAt = openedAt }));
    }

    // A manager holding one sample project, for a headless render of a surface that shows projects — the
    // parameterless constructor is deliberately empty, and an empty list renders the "no projects yet" state
    // instead of the rows under test. Mirrors `TtyViewModel.DesignTerminal`.
    internal static ProjectsViewModel DesignSample()
    {
        var viewModel = new ProjectsViewModel();
        viewModel._settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with
        {
            Description = "The cockpit itself — the desktop app these sessions run in.",
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            DefaultProfileLabel = "personal",
            LastOpenedAt = new DateTimeOffset(2026, 7, 26, 9, 30, 0, TimeSpan.FromHours(2)),
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/ai-cockpit"),
                new ProjectInfoField("Customer", "Acme BV — ask for their project lead"),
            ],
        });
        viewModel._Republish();

        return viewModel;
    }

    // `DesignSample` plus shared-project groups (AC-245), staged directly rather than through
    // `LoadSharedProjectsAsync` — there is no `ISharedProjectSource` or host in a headless
    // render, the same reason `_ProjectEditorWithMemorySourceReachability` (Screenshotter) stages its own
    // state directly instead of going through a real check delegate. One group with two rows (a name/description/
    // role each), one group carrying an error instead — the two states the workspace actually draws differently.
    internal static ProjectsViewModel DesignSampleWithSharedProjects()
    {
        var viewModel = DesignSample();
        viewModel.SharedProjectGroups.Add(new SharedProjectGroupViewModel(
            "Depot — Work",
            [
                new SharedProject("depot:onboarding", "Onboarding flow")
                {
                    Description = "New-hire checklist and the tooling walkthrough.",
                    Role = "Editor",
                },
                new SharedProject("depot:roadmap", "Product roadmap") { Role = "Viewer" },
            ],
            Error: null));
        viewModel.SharedProjectGroups.Add(new SharedProjectGroupViewModel(
            "Depot — Personal", [], "Sign in to this Depot connection to see its shared projects."));

        return viewModel;
    }

    // Categories (AC-618) as the list's main grouping — "Werk" before "Privé", not alphabetical, from an explicit
    // `ProjectSettings.CategoryOrder` rather than either name's own spelling — with "Uncategorized"
    // always last, shown even though nothing is in it here, and every card's own origin badge instead of AC-245's
    // retired "On this machine" heading: "Onboarding flow" carries a real `ProjectOwnershipRegistry`
    // claim, the same seam AC-604/AC-245 use, so it draws "◆ Depot — Work" rather than "● This machine".
    internal static ProjectsViewModel DesignSampleWithCategories()
    {
        var cockpit = Project.Create("Cockpit") with
        {
            Description = "The cockpit itself — the desktop app these sessions run in.",
            SourceDirectory = "/home/raymond/RiderProjects/AI-Cockpit",
            Category = "Privé",
        };
        var eveWorkbench = Project.Create("EVE Workbench") with
        {
            Description = "Community platform for fits and market.",
            SourceDirectory = "/home/raymond/RiderProjects/Eveworkbench",
            Category = "Privé",
        };
        var onboarding = Project.Create("Onboarding flow") with
        {
            Description = "New-hire checklist and the tooling walkthrough.",
            SourceDirectory = "/home/raymond/work/onboarding",
            Category = "Werk",
            MemoryRef = "depot:onboarding",
        };
        var scratch = Project.Create("Testproject") with { SourceDirectory = "/home/raymond/tmp/scratch" };

        var ownership = new ProjectOwnershipRegistry();
        ownership.Register(new ProjectOwnershipRegistration(onboarding.Id, new ProjectFieldOwnership("Depot — Work")));

        var viewModel = new ProjectsViewModel(new DesignTimeProjectStore(), dialogs: null, ownership: ownership);
        viewModel._settings = ProjectSettings.Empty with
        {
            Projects = [cockpit, eveWorkbench, onboarding, scratch],
            CategoryOrder = ["Werk", "Privé"],
        };
        viewModel._Republish();

        // AC-709: a selected card so this scene also renders the workspace's own selection styling, not just
        // its category grouping.
        viewModel.SelectedProject = viewModel.Projects.First(project => project.Id == eveWorkbench.Id);

        return viewModel;
    }

    // Reads the saved projects. Called when Options opens, so an edit made elsewhere is reflected rather than
    // overwritten. Deliberately does not wait on `LoadSharedProjectsAsync` — it only starts it: the
    // caller (`SessionDialogService.ShowProjectsDialogAsync`) awaits this before the workspace window is
    // even constructed, and a Depot connection that is slow or unreachable must not hold that window closed while
    // the local projects it already has sit ready.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _Republish();

        _BeginSharedProjectsLoad();
    }

    // Fills `SharedProjectGroups` from every registered `ISharedProjectSource` (AC-245).
    // Public (rather than folded into `LoadAsync`) so a test can await it directly instead of racing
    // the fire-and-forget call `LoadAsync` makes.
    //
    // A shared project already bound to a local one (its `SharedProject.Id` matches a
    // `ProjectResourceRole.Memory` row somewhere in `ProjectSettings.Projects`) is left out
    // — it already shows under "On this machine" — and, for that local project, has its origin claimed through
    // `IProjectOwnershipRegistry` (AC-604's own seam) so the editor can draw the ◆ Shared badge on it.
    // This runs from here rather than from the plugin that contributed the source: a plugin never sees the local
    // project list (it would have to reference `Cockpit.Core`, which no plugin project does), while this view
    // model already loaded both lists a moment ago.
    // A shared project hidden on this machine (`ProjectSettings.HiddenSharedProjectIds`) is left out
    // the same way. One source failing (not signed in, unreachable, timed out) only empties its own group — every
    // other source's rows are unaffected — and an empty, error-free group is left out entirely rather than shown
    // as a heading over nothing.
    public async Task LoadSharedProjectsAsync(CancellationToken cancellationToken = default)
    {
        if (_sharedSources is null)
        {
            return;
        }

        var sources = _sharedSources.Sources;
        if (sources.Count == 0)
        {
            SharedProjectGroups.Clear();
            return;
        }

        var boundIds = new HashSet<string>(
            _settings.Projects
                .SelectMany(project => project.Resources)
                .Where(resource => resource.Role == ProjectResourceRole.Memory)
                .Select(resource => resource.Reference),
            StringComparer.Ordinal);
        var hiddenIds = new HashSet<string>(_settings.HiddenSharedProjectIds, StringComparer.Ordinal);

        var results = await Task.WhenAll(sources.Select(source => _ListWithTimeoutAsync(source, cancellationToken)))
            .ConfigureAwait(true);

        if (cancellationToken.IsCancellationRequested)
        {
            // A newer LoadSharedProjectsAsync call superseded this one (LoadAsync cancels the previous token) —
            // its own results, not this stale run's, belong in SharedProjectGroups.
            return;
        }

        var groups = new List<SharedProjectGroupViewModel>();
        var reconciledProjects = new List<Project>();
        foreach (var (source, result) in sources.Zip(results))
        {
            // Claiming runs over every project this source reported, bound or not — a project already bound is
            // exactly the one this claim is for (it is what makes the editor draw the ◆ Shared badge on it), even
            // though it never appears in this source's own group below (it already shows under "On this machine").
            if (result.Succeeded)
            {
                reconciledProjects.AddRange(_ReconcileSharedSourceClaims(result.Projects, source));
            }

            var visible = result.Succeeded
                ? result.Projects.Where(project => !boundIds.Contains(project.Id) && !hiddenIds.Contains(project.Id)).ToList()
                : [];

            if (visible.Count == 0 && result.Succeeded)
            {
                continue;
            }

            groups.Add(new SharedProjectGroupViewModel(source.SourceName, visible, result.Succeeded ? null : result.Error));
        }

        if (reconciledProjects.Count > 0)
        {
            var reconciled = _settings;
            foreach (var project in reconciledProjects)
            {
                reconciled = reconciled.WithUpdated(project);
            }

            await _PersistAsync(reconciled).ConfigureAwait(true);
        }

        SharedProjectGroups.Clear();
        foreach (var group in groups)
        {
            SharedProjectGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasSharedProjects));
        OnPropertyChanged(nameof(HasNothingToShow));
        OnPropertyChanged(nameof(HasNoSharedProjectSources));

        // AC-618: _ClaimBoundProjects above may just have registered an ownership claim this run — a bound
        // project's card must show its "◆ <connection>" badge without the operator having to touch anything else
        // for _Republish to run again.
        _RepublishCategoryGroups();
    }

    // The "Finish setting up…" bind step (AC-246) for `sharedProject`, one of `SharedProjectGroups`'s
    // own rows. Finds the `ISharedProjectSource` it came from by its own `SharedProject.Id`
    // prefix (the same `"{scheme}:{slug}"` shape `ISharedProjectSource.Key`'s own doc comment
    // describes) rather than carrying the source alongside the row — `SharedProjectGroupViewModel` is
    // AC-245's own type and this ticket does not widen it.
    [RelayCommand]
    private async Task FinishSettingUpAsync(SharedProject sharedProject)
    {
        if (_dialogs is null || _sharedSources is null)
        {
            return;
        }

        var group = SharedProjectGroups.FirstOrDefault(candidate => candidate.Projects.Any(project => project.Id == sharedProject.Id));
        var source = _sharedSources.Sources.FirstOrDefault(
            candidate => sharedProject.Id.StartsWith(candidate.Key + ":", StringComparison.Ordinal));
        if (source is null || group is null)
        {
            return;
        }

        if (await _dialogs.ShowSharedProjectBindingDialogAsync(sharedProject, group.SourceName, source) is { } created)
        {
            var stored = await _WithStoredLogoAsync(created);
            await _PersistAsync(_settings.WithProject(stored));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);

            // The row just bound must not keep showing under "Shared via …" until the next full reload — a fresh
            // run finds it in boundIds now (its own Memory row names sharedProject.Id) and leaves it out on its own,
            // but that reload has not happened yet and the operator is looking at this list right now.
            await LoadSharedProjectsAsync();
        }
    }

    // Claims every local project bound to `sharedProjects` as owned by `source`, and returns every project whose
    // `SharedSourceName` needs persisting to match (AC-762): confirmed when still listed, cleared only when a
    // project's own claim names this exact `source` but its successful list no longer contains it.
    private List<Project> _ReconcileSharedSourceClaims(IReadOnlyList<SharedProject> sharedProjects, ISharedProjectSource source)
    {
        var byId = sharedProjects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var updated = new List<Project>();

        foreach (var project in _settings.Projects)
        {
            var boundTo = project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
            if (boundTo is not { Length: > 0 })
            {
                continue;
            }

            if (byId.TryGetValue(boundTo, out var sharedProject))
            {
                // AC-247/AC-763: every claimed field, Logo included, unlocks once the source says this role can
                // write (SharedProject.CanWriteBack) — SaveAsync now has somewhere to send that edit
                // (ISharedProjectSource.WriteBackAsync), so no per-field override is needed any more.
                _ownership?.Register(new ProjectOwnershipRegistration(
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

    // One source's projects, or a timeout failure if it does not answer within `_SharedProjectSourceTimeout`
    // — and, defensively, a failure if the source throws instead of reporting one through
    // `SharedProjectListResult.Failed` as its own contract asks. Never throws: a call superseded by a
    // newer `LoadSharedProjectsAsync` (its `cancellationToken` cancelled) also lands
    // here as an (ignored — see that method's own stale-result check) failure rather than an unobserved exception
    // on this fire-and-forget call.
    private static async Task<SharedProjectListResult> _ListWithTimeoutAsync(ISharedProjectSource source, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var listTask = source.ListAsync(timeoutCts.Token);
            var completed = await Task.WhenAny(listTask, Task.Delay(SharedProjectSourceTimeout, cancellationToken)).ConfigureAwait(true);
            if (completed != listTask)
            {
                timeoutCts.Cancel();
                return SharedProjectListResult.Failed("Timed out waiting for a response.");
            }

            return await listTask.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            return SharedProjectListResult.Failed(exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddProjectAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        if (await _dialogs.ShowProjectDialogAsync(null) is { } created)
        {
            var stored = await _WithStoredLogoAsync(created);
            await _PersistAsync(_settings.WithProject(stored));
            SelectedProject = Projects.FirstOrDefault(project => project.Id == stored.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task EditProjectAsync() =>
        SelectedProject is { } project ? EditAsync(project) : Task.CompletedTask;

    // Opens the editor for `project` and saves what comes back. Public because the sidebar
    // (AC-164) edits the project under the pointer rather than a selection — one editing path either way, so a
    // project edited from the sidebar and one edited from Options are written the same.
    public async Task EditAsync(Project project)
    {
        if (_dialogs is null)
        {
            return;
        }

        if (await _dialogs.ShowProjectDialogAsync(project, _ResolveSharedSource(project)) is { } edited)
        {
            var stored = await _WithStoredLogoAsync(edited);
            await _PersistAsync(_settings.WithUpdated(stored));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);
        }
    }

    // The source `project` is genuinely bound to — a matching Memory-reference prefix alone is not enough (AC-744).
    // "Claimed" is a live ownership claim or, absent that (AC-762), the persisted `SharedSourceName` — same
    // either/or `_OriginBadge` reads, so the share toggle never disagrees with what the badge just showed.
    private ISharedProjectSource? _ResolveSharedSource(Project project)
    {
        if (_sharedSources is null)
        {
            return null;
        }

        var isClaimed = _ownership?.Resolve(project.Id) is not null || project.SharedSourceName is { Length: > 0 };
        if (!isClaimed)
        {
            return null;
        }

        var boundTo = project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
        return boundTo is { Length: > 0 }
            ? _sharedSources.Sources.FirstOrDefault(source => boundTo.StartsWith(source.Key + ":", StringComparison.Ordinal))
            : null;
    }

    // Removes a project after confirming. Sessions already running under it keep running — a project is what a
    // session started with, not something it holds open, so removing one is not a reason to stop work in flight.
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveProjectAsync()
    {
        if (_dialogs is null || SelectedProject is not { } project)
        {
            return;
        }

        var confirmed = await _dialogs.ShowConfirmationDialogAsync(
            "Remove project",
            $"Remove ‘{project.Name}’? Sessions already running under it are unaffected.");

        if (confirmed)
        {
            _logos?.Remove(project.Id);
            await _PersistAsync(_settings.WithoutProject(project.Id));
        }
    }

    // `project` with its logo as a copy the cockpit owns. The editor hands back whatever the
    // operator pointed at — a file, a URL, or the path of the copy already stored; this turns the first two into
    // a copy, leaves the third alone, and drops the stored one when the field was cleared. A source that cannot be
    // read costs the picture and not the save: a project is not worth less for a logo that would not load.
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

        return project with { LogoPath = await _logos.SaveAsync(project.Id, source) };
    }

    private async Task _PersistAsync(ProjectSettings settings)
    {
        _settings = settings;
        await _store.SaveAsync(settings).ConfigureAwait(true);
        _Republish();
    }

    private void _Republish()
    {
        var selectedId = SelectedProject?.Id;

        Projects.Clear();
        foreach (var project in _settings.Projects)
        {
            Projects.Add(project);
        }

        RecentProjects.Clear();
        foreach (var project in _settings.Projects
            .OrderByDescending(project => project.LastOpenedAt ?? DateTimeOffset.MinValue)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            RecentProjects.Add(project);
        }

        SidebarProjects.Clear();
        foreach (var project in RecentProjects.Take(SidebarLimit))
        {
            SidebarProjects.Add(project);
        }

        SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedId);
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasNothingToShow));
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(OpenedProjectCount));
        OnPropertyChanged(nameof(MostRecentProject));
        OnPropertyChanged(nameof(HasMoreThanSidebarShows));

        _RepublishCategoryGroups();
    }

    // Rebuilds `ProjectCategoryGroups` (AC-618) from a locally normalized view of `_settings`
    // — `ProjectSettings.Normalized` is read here rather than trusted to already be reflected in
    // `_settings` itself (`_PersistAsync` assigns the settings it was handed, not what
    // `IProjectStore.SaveAsync` actually normalized and wrote), so a category typed for the first time
    // this save shows its heading immediately instead of one republish cycle late. Reading it this way changes
    // nothing about `_settings` itself or what gets persisted.
    private void _RepublishCategoryGroups()
    {
        ProjectCategoryGroups.Clear();

        var normalized = _settings.Normalized();
        if (!normalized.Projects.Any(project => !string.IsNullOrWhiteSpace(project.Category)))
        {
            ProjectCategoryGroups.Add(new ProjectCategoryGroupViewModel(CategoryName: null, [.. normalized.Projects.Select(_ToCard)]));
            return;
        }

        foreach (var category in normalized.CategoryOrder)
        {
            var cards = normalized.Projects
                .Where(project => string.Equals(project.Category, category, StringComparison.OrdinalIgnoreCase))
                .Select(_ToCard)
                .ToList();
            ProjectCategoryGroups.Add(new ProjectCategoryGroupViewModel(category, cards));
        }

        var uncategorized = normalized.Projects
            .Where(project => string.IsNullOrWhiteSpace(project.Category))
            .Select(_ToCard)
            .ToList();
        ProjectCategoryGroups.Add(new ProjectCategoryGroupViewModel(_UncategorizedLabel, uncategorized));
    }

    private ProjectCardViewModel _ToCard(Project project) =>
        new(project, _OriginBadge(project)) { IsSelected = project.Id == SelectedProject?.Id };

    // "● This machine", or "◆ &lt;connection&gt;" once `_ownership` has a claim on `project` (AC-604, claimed by
    // `_ReconcileSharedSourceClaims`) — falls back to `SharedSourceName` (AC-762) so a genuinely shared project
    // never renders as local just because that in-memory, network-rebuilt claim has not arrived or failed.
    private string _OriginBadge(Project project) =>
        _ownership?.Resolve(project.Id)?.Values.FirstOrDefault(ownership => ownership is not null) is { } claim
            ? $"◆ {claim.SourceName}"
            : project.SharedSourceName is { Length: > 0 } lastKnown
                ? $"◆ {lastKnown}"
                : "● This machine";

    partial void OnSelectedProjectChanged(Project? value)
    {
        EditProjectCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
        ToggleSharingCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShareToggleLabel));

        // AC-709: keeps every already-materialized card's selected style in sync — a plain assignment here
        // (rather than going through a full _Republish) is what OnProjectPressed does on every click.
        foreach (var card in ProjectCategoryGroups.SelectMany(group => group.Cards))
        {
            card.IsSelected = card.Project.Id == value?.Id;
        }
    }

    // AC-620: one button, two directions — "Share…" opens the confirmation screen for a local project, "Stop
    // sharing…" removes the local binding of one already shared. Never both/neither: a project is exactly one of
    // the two, the same either/or `_ResolveSharedSource` already answers for AC-247's write-back gating.
    public string ShareToggleLabel => SelectedProject is { } project && _ResolveSharedSource(project) is not null
        ? "Stop sharing…"
        : "Share…";

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ToggleSharingAsync() => SelectedProject is { } project ? ToggleSharingAsync(project) : Task.CompletedTask;

    // The launcher's own per-card button (AC-620) has no selection to read — each card acts on its own project —
    // so this is public and parameterized, the same split EditAsync(Project) already keeps from
    // EditProjectAsync()'s selection-based command above.
    public async Task ToggleSharingAsync(Project project)
    {
        if (_ResolveSharedSource(project) is not null)
        {
            await _StopSharingAsync(project);
        }
        else
        {
            await _ShareAsync(project);
        }
    }

    // AC-620's publication naad: offers every registered source that can publish, generically — the host does not
    // know or care that it is Depot. No connection able to publish yet → nothing to open.
    private async Task _ShareAsync(Project project)
    {
        if (_dialogs is null || _sharedSources is null)
        {
            return;
        }

        var publishSources = _sharedSources.Sources.Where(source => source.CanPublish).ToList();
        if (publishSources.Count == 0)
        {
            return;
        }

        if (await _dialogs.ShowShareProjectDialogAsync(project, publishSources) is { } shared)
        {
            var stored = await _WithStoredLogoAsync(shared);
            await _PersistAsync(_settings.WithUpdated(stored));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);
            await LoadSharedProjectsAsync();
        }
    }

    // Removes only the local binding — the first Memory-role resource _ResolveSharedSource reads. `.cockpit/project.json`
    // itself is never touched: a colleague's own binding stays unaffected (Raymond's decision, explicit confirmation text).
    private async Task _StopSharingAsync(Project project)
    {
        if (_dialogs is null)
        {
            return;
        }

        var confirmed = await _dialogs.ShowConfirmationDialogAsync(
            "Stop sharing?",
            $"This only removes the connection ‘{project.Name}’ has on this machine. Nothing is deleted in Depot — the shared definition and every colleague's own binding stay exactly as they are.",
            confirmLabel: "Stop sharing");

        if (!confirmed)
        {
            return;
        }

        var resources = project.Resources.ToList();
        var index = resources.FindIndex(resource => resource.Role == ProjectResourceRole.Memory);
        if (index < 0)
        {
            return;
        }

        var withoutBinding = project with
        {
            Resources = [.. resources.Take(index), .. resources.Skip(index + 1)],
            // AC-762: the cold-start fallback must lose the badge here too, not only the live claim.
            SharedSourceName = null,
        };

        await _PersistAsync(_settings.WithUpdated(withoutBinding));
        SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == withoutBinding.Id);
        await LoadSharedProjectsAsync();
    }
}
