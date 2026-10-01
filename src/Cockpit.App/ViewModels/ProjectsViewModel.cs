using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// Hands what `ProjectDialogViewModel` produces to IProjectEditor, so the editor stays a value editor (AC-161, AC-245),
// and draws IProjectCatalog's snapshot, also after a write it did not make itself (AC-1435).
public partial class ProjectsViewModel : ViewModelBase, ISingletonService
{
    private readonly IProjectCatalog _catalog;
    private readonly IProjectEditor _editor;
    private ProjectCatalogSnapshot _snapshot = ProjectCatalogSnapshot.Empty;

    // AC-490: the job-run trail the cards read "started 9 September" from, and what was read from it last. Null
    // in a graph without one, where every job's line stays the calendar's alone.
    private readonly IProjectJobHistory? _jobHistory;
    private IReadOnlyList<ProjectJobRun> _jobRuns = [];

    // Null only under the previewer, which has no window to open a dialog over; every command that needs one is inert there.
    private readonly ISessionDialogService? _dialogs;

    // Which layout the Projects page draws (AC-772). Null under the previewer, which keeps the default and persists nothing.
    private readonly IProjectsDisplaySettingsStore? _displaySettings;


    // What the cards this view model builds can do (AC-772). Set once by `CockpitViewModel` — which owns the
    // commands — before the first load, so every card carries them. Left null under the previewer and in tests,
    // where a card is data to inspect rather than a thing to start.
    internal ProjectCardActions? CardActions { get; set; }

    // The background shared-project read `LoadAsync` most recently started
    // (never awaited by it — see that method's own remarks). Internal test seam only: it lets a test await the
    // same run `LoadAsync` kicked off instead of racing it with a second, independent call.
    internal Task SharedProjectsLoadTask { get; private set; } = Task.CompletedTask;

    // Design-time constructor for the Avalonia previewer: projects kept in memory and no dialog service, so a rendered
    // surface can reach neither the operator's config nor a window that does not exist there. The commands are inert
    // in that context — see `_dialogs`.
    public ProjectsViewModel()
        : this(new DesignTimeProjects(), dialogs: null)
    {
    }

    private ProjectsViewModel(DesignTimeProjects projects, ISessionDialogService? dialogs)
        : this(projects, projects, dialogs)
    {
    }

    public ProjectsViewModel(
        IProjectCatalog catalog,
        IProjectEditor editor,
        ISessionDialogService? dialogs,
        IProjectsDisplaySettingsStore? displaySettings = null,
        IProjectJobHistory? jobHistory = null)
    {
        _catalog = catalog;
        _editor = editor;
        _dialogs = dialogs;
        _displaySettings = displaySettings;
        _jobHistory = jobHistory;

        // AC-1435: a write by the assistant, a shared-project read or a Depot sync check lands on another thread.
        _catalog.Changed += _OnCatalogChanged;
    }

    private void _OnCatalogChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _ApplyCurrent();
        }
        else
        {
            Dispatcher.UIThread.Post(_ApplyCurrent);
        }
    }

    // Draws the catalog's latest snapshot. Also called right after this view model's own write, so a selection made
    // straight after it finds the project it just stored.
    private void _ApplyCurrent()
    {
        if (ReferenceEquals(_catalog.Current, _snapshot))
        {
            return;
        }

        var sharedGroupsChanged = !ReferenceEquals(_catalog.Current.SharedProjectGroups, _snapshot.SharedProjectGroups);
        _snapshot = _catalog.Current;
        _Republish();
        if (sharedGroupsChanged)
        {
            _RepublishSharedProjectGroups();
        }
    }

    // Starts a shared-project read without waiting on it: opening the workspace never blocks on a slow connection.
    private void _BeginSharedProjectsLoad() => SharedProjectsLoadTask = LoadSharedProjectsAsync();

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

    // Empty until `LoadSharedProjectsAsync` has run at least once; `LoadAsync` starts it in the background rather than
    // waiting on it, so opening the workspace never blocks on a slow or unreachable connection — see that method's own
    // remarks (AC-245).
    public ObservableCollection<SharedProjectGroupViewModel> SharedProjectGroups { get; } = [];

    // Whether there is anything to show under a "Shared" heading right now — lets the workspace leave the whole section out rather than draw an empty one.
    public bool HasSharedProjects => SharedProjectGroups.Count > 0;

    // AC-248: gates the launcher's own pointer line separately from HasSharedProjects, so it never contradicts a
    // signed-out connection's own "Sign in to this Depot connection…" error by implying nothing is set up.
    public bool HasNoSharedProjectSources => _snapshot.Sources.Count == 0;

    // `Projects` grouped by category for the list (AC-618), rebuilt by `_Republish` — replaces AC-245's "On this
    // machine" heading with a per-card origin badge instead (`ProjectCardViewModel.OriginBadge`).
    public ObservableCollection<ProjectCategoryGroupViewModel> ProjectCategoryGroups { get; } = [];

    // The same cards the groups above hold, in "last worked on" order — what the Continue layout draws (AC-772).
    // Rebuilt alongside them by `_RepublishRecentCards`, so the two never disagree about which projects exist.
    public ObservableCollection<ProjectCardViewModel> RecentCards { get; } = [];

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

    // `ProjectCount` and `OpenedProjectCount` fed the overview's summary line, which AC-772 removed: it was a
    // dashboard gesture in front of the projects you came for, answering no question that screen raises. Nothing
    // reads them any more, so they went with it.

    // The project a session was last started on, or null when none ever was.
    public Project? MostRecentProject => RecentProjects.FirstOrDefault(project => project.LastOpenedAt is not null);

    // Records that a session just started on `project`, so the overview can lead with what is
    // actually worked on. A project removed in the meantime is left alone rather than written back. True when the
    // project was saved — AC-490 records a job run only after that.
    public async Task<bool> MarkOpenedAsync(Project project, DateTimeOffset openedAt)
    {
        var marked = await _editor.MarkOpenedAsync(project.Id, openedAt);
        _ApplyCurrent();
        return marked;
    }

    // A manager holding one sample project, for a headless render of a surface that shows projects — the
    // parameterless constructor is deliberately empty, and an empty list renders the "no projects yet" state
    // instead of the rows under test. Mirrors `TtyViewModel.DesignTerminal`.
    internal static ProjectsViewModel DesignSample()
    {
        var viewModel = new ProjectsViewModel();
        viewModel.StageDesignSample();

        return viewModel;
    }

    // The same sample, applied to an existing view model — for the Projects-workspace renders (AC-772), where the
    // view model is the one `CockpitViewModel` already owns and so cannot be swapped for a freshly built sample.
    internal void StageDesignSample()
    {
        _snapshot = _snapshot with
        {
            Settings = ProjectSettings.Empty.WithProject(Project.Create("Cockpit") with
        {
            Description = "The cockpit itself — the desktop app these sessions run in.",
            SourceDirectories = [new("/home/raymond/RiderProjects/AI-Cockpit")],
            DefaultProfileLabel = "personal",
            LastOpenedAt = new DateTimeOffset(2026, 7, 26, 9, 30, 0, TimeSpan.FromHours(2)),
            AdditionalInfo =
            [
                new ProjectInfoField("Repository", "https://github.com/example/ai-cockpit"),
                new ProjectInfoField("Customer", "Acme BV — ask for their project lead"),
            ],
        }),
        };
        _Republish();
    }

    // `DesignSample` plus shared-project groups (AC-245), staged directly rather than through
    // `LoadSharedProjectsAsync`.
    internal static ProjectsViewModel DesignSampleWithSharedProjects()
    {
        var viewModel = DesignSample();
        viewModel.StageDesignSharedProjects();

        return viewModel;
    }

    // As `StageDesignSample`, for the shared-project groups — see that method for why both exist as instance methods.
    internal void StageDesignSharedProjects()
    {
        SharedProjectGroups.Add(new SharedProjectGroupViewModel(
            "Depot — Work",
            [
                new SharedProjectOffer("depot:onboarding", "Onboarding flow", "New-hire checklist and the tooling walkthrough.", "Editor"),
                new SharedProjectOffer("depot:roadmap", "Product roadmap", Role: "Viewer"),
            ],
            Error: null));
        SharedProjectGroups.Add(new SharedProjectGroupViewModel(
            "Depot — Personal", [], "Sign in to this Depot connection to see its shared projects."));

        OnPropertyChanged(nameof(HasSharedProjects));
        OnPropertyChanged(nameof(HasNothingToShow));
    }

    // Categories (AC-618) as the list's main grouping (AC-245, AC-604).
    internal static ProjectsViewModel DesignSampleWithCategories()
    {
        var cockpit = Project.Create("Cockpit") with
        {
            Description = "The cockpit itself — the desktop app these sessions run in.",
            SourceDirectories = [new("/home/raymond/RiderProjects/AI-Cockpit")],
            Category = "Privé",
        };
        var eveWorkbench = Project.Create("EVE Workbench") with
        {
            Description = "Community platform for fits and market.",
            SourceDirectories = [new("/home/raymond/RiderProjects/Eveworkbench")],
            Category = "Privé",
        };
        var onboarding = Project.Create("Onboarding flow") with
        {
            Description = "New-hire checklist and the tooling walkthrough.",
            SourceDirectories = [new("/home/raymond/work/onboarding")],
            Category = "Werk",
            MemoryRef = "depot:onboarding",
        };
        var scratch = Project.Create("Testproject") with { SourceDirectories = [new("/home/raymond/tmp/scratch")] };

        var viewModel = new ProjectsViewModel();
        viewModel._snapshot = ProjectCatalogSnapshot.Empty with
        {
            Settings = ProjectSettings.Empty with
            {
                Projects = [cockpit, eveWorkbench, onboarding, scratch],
                CategoryOrder = ["Werk", "Privé"],
            },
            OwnershipClaims = new Dictionary<string, string?> { [onboarding.Id] = "Depot — Work" },
        };
        viewModel._Republish();

        // AC-709: a selected card so this scene also renders the workspace's own selection styling, not just
        // its category grouping.
        viewModel.SelectedProject = viewModel.Projects.First(project => project.Id == eveWorkbench.Id);

        return viewModel;
    }

    // Called when Options opens, so an edit made elsewhere is reflected rather than overwritten.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.LoadAsync(cancellationToken).ConfigureAwait(true);
        _snapshot = _catalog.Current;

        await RefreshJobRunsAsync(cancellationToken).ConfigureAwait(true);

        if (_displaySettings is not null)
        {
            // Deliberately unguarded: it reads the same cockpit.json the project store read a line above, so an
            // unreadable file has already thrown and a catch here would cover a case it cannot reach.
            LayoutMode = (await _displaySettings.LoadAsync(cancellationToken).ConfigureAwait(true)).LayoutMode;
        }

        _BeginSharedProjectsLoad();
    }

    // Which layout the page draws (AC-772). Persisted per operator by `SetLayoutModeAsync`; the segmented control on
    // the page itself is the only place it is set, deliberately — a preference you can see the effect of while you
    // change it does not need a settings screen to hide in.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCardsLayout))]
    [NotifyPropertyChangedFor(nameof(IsListLayout))]
    [NotifyPropertyChangedFor(nameof(IsContinueLayout))]
    private ProjectsLayoutMode _layoutMode = ProjectsLayoutMode.Cards;

    public bool IsCardsLayout => LayoutMode == ProjectsLayoutMode.Cards;

    public bool IsListLayout => LayoutMode == ProjectsLayoutMode.List;

    public bool IsContinueLayout => LayoutMode == ProjectsLayoutMode.Continue;

    // Whether the third segment is offered at all — see `ProjectsDisplaySettings.ContinueLayoutAvailable`.
    public static bool ShowContinueLayoutOption => ProjectsDisplaySettings.ContinueLayoutAvailable;

    // Switches the page's layout and remembers it. Takes the mode by name so the three segments can be one command
    // with a parameter rather than three commands that differ in one word.
    [RelayCommand]
    private async Task SetLayoutModeAsync(string? mode)
    {
        if (!Enum.TryParse<ProjectsLayoutMode>(mode, ignoreCase: true, out var parsed))
        {
            return;
        }

        var normalized = new ProjectsDisplaySettings { LayoutMode = parsed }.Normalized();
        if (normalized.LayoutMode == LayoutMode)
        {
            // Clicking the segment you are already on: a ToggleButton has flipped its own IsChecked to false by the
            // time this runs, so returning without a word would leave the control with nothing selected. Re-raising
            // is what pushes the one-way binding back over that local flip.
            _NotifyLayoutSegments();

            return;
        }

        LayoutMode = normalized.LayoutMode;

        if (_displaySettings is not null)
        {
            try
            {
                await _displaySettings.SaveAsync(normalized).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // The write, unlike the read in `LoadAsync`, can fail on its own — a read-only disk, a permission
                // change, a config write gate — and it does so from a command, where an exception has no caller to
                // land on. The page has already switched by then; the choice simply does not survive a restart.
            }
        }
    }

    // The three segments read off `LayoutMode` through separate computed properties, so a re-assert has to name all
    // three — the one turning on and the two turning off.
    private void _NotifyLayoutSegments()
    {
        OnPropertyChanged(nameof(IsCardsLayout));
        OnPropertyChanged(nameof(IsListLayout));
        OnPropertyChanged(nameof(IsContinueLayout));
    }

    // Public (rather than folded into `LoadAsync`) so a test can await it directly instead of racing the
    // fire-and-forget call `LoadAsync` makes (AC-245, AC-604). The catalog reads the sources and claims (AC-1435).
    public async Task LoadSharedProjectsAsync(CancellationToken cancellationToken = default)
    {
        await _catalog.RefreshSharedProjectsAsync(cancellationToken).ConfigureAwait(true);
        _ApplyCurrent();
    }

    private void _RepublishSharedProjectGroups()
    {
        SharedProjectGroups.Clear();
        foreach (var group in _snapshot.SharedProjectGroups)
        {
            SharedProjectGroups.Add(new SharedProjectGroupViewModel(group.SourceName, group.Projects, group.Error));
        }

        OnPropertyChanged(nameof(HasSharedProjects));
        OnPropertyChanged(nameof(HasNothingToShow));
        OnPropertyChanged(nameof(HasNoSharedProjectSources));
    }

    // Finds the source it came from by its own `SharedProjectOffer.Id` prefix (the same `"{scheme}:{slug}"` shape
    // `ISharedProjectSource.Key`'s own doc comment describes) rather than carrying the source alongside the row
    // (AC-246, AC-245).
    [RelayCommand]
    private async Task FinishSettingUpAsync(SharedProjectOffer sharedProject)
    {
        if (_dialogs is null)
        {
            return;
        }

        var group = SharedProjectGroups.FirstOrDefault(candidate => candidate.Projects.Any(project => project.Id == sharedProject.Id));
        var source = _snapshot.Sources.FirstOrDefault(
            candidate => sharedProject.Id.StartsWith(candidate.Key + ":", StringComparison.Ordinal));
        if (source is null || group is null)
        {
            return;
        }

        if (await _dialogs.ShowSharedProjectBindingDialogAsync(sharedProject.Id, group.SourceName, source.Key) is { } created)
        {
            var stored = await AddBoundProjectAsync(created);

            // Selecting it belongs to this route and not to the shared tail: the operator just filled in a dialog
            // for this project, so it is what they are looking at. On the assistant's own route nobody clicked, and
            // moving the selection would take it out from under whatever they had picked in Manage projects.
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);
        }
    }

    // Stores `created`, a project just built from a shared definition, the way the assistant's bind does (AC-798):
    // both go through IProjectEditor.AddBoundProjectAsync.
    internal async Task<Project> AddBoundProjectAsync(Project created)
    {
        var stored = await _editor.AddBoundProjectAsync(created);
        _ApplyCurrent();
        return stored;
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
            var stored = await AddNewProjectAsync(created);
            SelectedProject = Projects.FirstOrDefault(project => project.Id == stored.Id);
        }
    }

    // AC-488: the starting points the overview offers in place of an empty state. A fixed list rather than
    // anything stored — a starting point is a set of answers, not a thing the operator owns or can edit.
    public IReadOnlyList<ProjectStartingPoint> StartingPoints { get; } = ProjectStartingPoint.All;

    // AC-488: asks the one question a starting point cannot answer itself and saves what comes back. Never the
    // project editor — everything it would ask is answered, and that form is the step this gallery removes. What
    // lands is a project like any other because this goes through `AddNewProjectAsync`, the add door's own.
    [RelayCommand]
    private async Task UseStartingPointAsync(ProjectStartingPoint startingPoint)
    {
        if (_dialogs is null)
        {
            return;
        }

        if (await _dialogs.PickFolderAsync($"Choose {startingPoint.Needs}") is not { Length: > 0 } folder)
        {
            return;
        }

        var stored = await AddNewProjectAsync(startingPoint.Create(folder));
        SelectedProject = Projects.FirstOrDefault(project => project.Id == stored.Id);
    }

    // Two adds at once both land: the editor serialises its writes (AC-799).
    internal async Task<Project> AddNewProjectAsync(Project created)
    {
        var stored = await _editor.AddNewProjectAsync(created);
        _ApplyCurrent();
        return stored;
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

        if (await _dialogs.ShowProjectDialogAsync(project, _ResolveSharedSource(project)?.Key) is { } edited)
        {
            await _UpdateAsync(edited);
        }
    }

    // The source `project` is genuinely bound to — a matching Memory-reference prefix alone is not enough (AC-744).
    // "Claimed" is a live ownership claim or, absent that (AC-762), the persisted `SharedSourceName` — same
    // either/or `_OriginBadge` reads, so the share toggle never disagrees with what the badge just showed.
    private SharedProjectSourceInfo? _ResolveSharedSource(Project project)
    {
        var isClaimed = _snapshot.OwnershipClaims.ContainsKey(project.Id) || project.SharedSourceName is { Length: > 0 };
        if (!isClaimed)
        {
            return null;
        }

        var boundTo = project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
        return boundTo is { Length: > 0 }
            ? _snapshot.Sources.FirstOrDefault(source => boundTo.StartsWith(source.Key + ":", StringComparison.Ordinal))
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
            await _editor.RemoveProjectAsync(project.Id);
            _ApplyCurrent();
        }
    }

    // Stores `project` over its stored self and selects it.
    private async Task _UpdateAsync(Project project)
    {
        await _editor.UpdateStoredProjectAsync(project);
        _ApplyCurrent();
        SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == project.Id);
    }

    private void _Republish()
    {
        var selectedId = SelectedProject?.Id;

        Projects.Clear();
        foreach (var project in _snapshot.Settings.Projects)
        {
            Projects.Add(project);
        }

        RecentProjects.Clear();
        foreach (var project in _snapshot.Settings.Projects
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
        OnPropertyChanged(nameof(MostRecentProject));
        OnPropertyChanged(nameof(HasMoreThanSidebarShows));

        _RepublishCategoryGroups();
    }

    // Normalize locally before rebuilding so a newly typed category heading appears immediately (AC-618).
    private void _RepublishCategoryGroups()
    {
        ProjectCategoryGroups.Clear();

        var normalized = _snapshot.Settings.Normalized();
        _RepublishRecentCards(normalized);
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

    // The Continue layout's own order (AC-772): most recently worked on first, never-opened last. Flat rather than
    // grouped by category — this layout answers "what was I doing", and a category heading does not help with that.
    private void _RepublishRecentCards(ProjectSettings normalized)
    {
        RecentCards.Clear();
        var ordered = normalized.Projects
            .OrderByDescending(project => project.LastOpenedAt ?? DateTimeOffset.MinValue)
            .ThenBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(_ToCard);

        foreach (var card in ordered)
        {
            RecentCards.Add(card);
        }
    }

    private ProjectCardViewModel _ToCard(Project project) =>
        new(project, _OriginBadge(project), CardActions, _snapshot.RemoteChangedProjectIds.Contains(project.Id), jobRuns: _jobRuns)
        {
            IsSelected = project.Id == SelectedProject?.Id,
        };

    // AC-490: re-reads the job-run trail and rebuilds the cards on it. Called after a run's `Started` line is written,
    // since the project save that precedes it has already republished without that line.
    public async Task RefreshJobRunsAsync(CancellationToken cancellationToken = default)
    {
        if (_jobHistory is not null)
        {
            _jobRuns = await _jobHistory.ReadRecentRunsAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }

        _Republish();
    }

    // AC-894: "Sync now" checks one Depot-bound project immediately, outside the watcher's own interval.
    internal async Task SyncNowAsync(Project project)
    {
        await _catalog.SyncNowAsync(project.Id);
        _ApplyCurrent();
    }

    // "● This machine", or "◆ &lt;connection&gt;" once the catalog holds a claim on `project` (AC-604) — falls back to
    // `SharedSourceName` (AC-762) so a genuinely shared project never renders as local just because that in-memory,
    // network-rebuilt claim has not arrived or failed.
    private string _OriginBadge(Project project) =>
        _snapshot.OwnershipClaims.GetValueOrDefault(project.Id) is { } claim
            ? $"◆ {claim}"
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
        // (rather than going through a full _Republish) is what OnProjectPressed does on every click. AC-772:
        // `RecentCards` holds its own instances for the same projects, so it has to be reached too.
        var cards = ProjectCategoryGroups.SelectMany(group => group.Cards).Concat(RecentCards);
        foreach (var card in cards)
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
        if (_dialogs is null)
        {
            return;
        }

        var publishSources = _snapshot.Sources.Where(source => source.CanPublish).Select(source => source.Key).ToList();
        if (publishSources.Count == 0)
        {
            return;
        }

        if (await _dialogs.ShowShareProjectDialogAsync(project, publishSources) is { } shared)
        {
            await _UpdateAsync(shared);
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

        await _UpdateAsync(withoutBinding);
        await LoadSharedProjectsAsync();
    }
}
