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

/// <summary>
/// The projects manager behind Options → Projects (AC-161): the saved projects, and add/edit/remove over them.
/// Owns the persisting that <see cref="ProjectDialogViewModel"/> deliberately does not, so the editor stays a
/// value editor and this is the only thing that writes the list.
/// <para>
/// Since AC-245, also the Projects workspace's read side for what a plugin shares elsewhere but this machine has
/// not bound yet — see <see cref="SharedProjectGroups"/> and <see cref="LoadSharedProjectsAsync"/>.
/// </para>
/// </summary>
public partial class ProjectsViewModel : ViewModelBase, ISingletonService
{
    private readonly IProjectStore _store;

    /// <summary>Takes the cockpit's own copy of a picked or downloaded logo. Null under the previewer, where a project keeps whatever path it was given.</summary>
    private readonly IProjectLogoStore? _logos;

    /// <summary>Null only under the previewer, which has no window to open a dialog over; every command that needs one is inert there.</summary>
    private readonly ISessionDialogService? _dialogs;

    /// <summary>
    /// Where a shared project's origin is claimed for a local project already bound to it (AC-604/AC-245's own
    /// consumption of it — see <see cref="LoadSharedProjectsAsync"/>). Null under the previewer.
    /// </summary>
    private readonly IProjectOwnershipRegistry? _ownership;

    /// <summary>What every registered plugin shares elsewhere (AC-245). Null under the previewer, where <see cref="SharedProjectGroups"/> simply stays empty.</summary>
    private readonly ISharedProjectSourceRegistry? _sharedSources;

    private ProjectSettings _settings = ProjectSettings.Empty;

    /// <summary>Cancels a still-running <see cref="LoadSharedProjectsAsync"/> when a newer one starts (the workspace reopened, say), so a slow connection cannot overwrite a fresher answer with a stale one.</summary>
    private CancellationTokenSource? _sharedProjectsLoadCts;

    /// <summary>
    /// The background <see cref="LoadSharedProjectsAsync"/> call <see cref="LoadAsync"/> most recently started
    /// (never awaited by it — see that method's own remarks). Internal test seam only: it lets a test await the
    /// same run <see cref="LoadAsync"/> kicked off instead of racing it with a second, independent call.
    /// </summary>
    internal Task SharedProjectsLoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Design-time constructor for the Avalonia previewer: an empty store and no dialog service, so a rendered
    /// surface can reach neither the operator's config nor a window that does not exist there. The commands are
    /// inert in that context — see <see cref="_dialogs"/>.
    /// </summary>
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
    }

    /// <summary>The saved projects in the order they are stored — what the manager lists and edits.</summary>
    public ObservableCollection<Project> Projects { get; } = [];

    /// <summary>
    /// The same projects, most recently opened first and never-opened ones after them by name — what the overview
    /// leads with. A separate list rather than a re-sorted <see cref="Projects"/>: the manager's order is the
    /// operator's own, and re-ordering it under them every time a session starts would be its own small chaos.
    /// </summary>
    public ObservableCollection<Project> RecentProjects { get; } = [];

    /// <summary>
    /// The few most recently worked on, for the sidebar (Raymond, 2026-07-24): that strip is for reaching what you
    /// are busy with, and a list that grows with every project turns it back into a menu. The rest stay one click
    /// away in the overview.
    /// </summary>
    public ObservableCollection<Project> SidebarProjects { get; } = [];

    /// <summary>How many of them the sidebar shows.</summary>
    private const int SidebarLimit = 5;

    /// <summary>
    /// How long <see cref="LoadSharedProjectsAsync"/> waits on one source before treating it as failed — one slow
    /// or hung connection must not hold up every other source's rows, let alone the whole workspace. Internal and
    /// mutable (rather than a private constant) purely as a test seam: a real 10s wait has no place in a unit test
    /// that wants to prove the timeout path itself.
    /// </summary>
    internal static TimeSpan SharedProjectSourceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shared projects (AC-245), one group per registered <see cref="ISharedProjectSource"/> — "Shared via Depot —
    /// Work" — already bound projects and this machine's own hidden ones filtered out. Empty until
    /// <see cref="LoadSharedProjectsAsync"/> has run at least once; <see cref="LoadAsync"/> starts it in the
    /// background rather than waiting on it, so opening the workspace never blocks on a slow or unreachable
    /// connection — see that method's own remarks.
    /// </summary>
    public ObservableCollection<SharedProjectGroupViewModel> SharedProjectGroups { get; } = [];

    /// <summary>Whether there is anything to show under a "Shared" heading right now — lets the workspace leave the whole section out rather than draw an empty one.</summary>
    public bool HasSharedProjects => SharedProjectGroups.Count > 0;

    /// <summary>
    /// <see cref="Projects"/> grouped by category for the list (AC-618), rebuilt by <see cref="_Republish"/> —
    /// replaces AC-245's "On this machine" heading with a per-card origin badge instead
    /// (<see cref="ProjectCardViewModel.OriginBadge"/>). No project with a category anywhere means exactly one
    /// group with a null <see cref="ProjectCategoryGroupViewModel.CategoryName"/>, which the workspace draws with
    /// no heading at all — see that type's own remarks.
    /// </summary>
    public ObservableCollection<ProjectCategoryGroupViewModel> ProjectCategoryGroups { get; } = [];

    /// <summary>The always-present, never-disappearing catch-all category group's heading (AC-618).</summary>
    private const string _UncategorizedLabel = "Uncategorized";

    /// <summary>
    /// Whether the workspace has nothing at all to show — what the "No projects yet" empty state is gated on
    /// instead of <c>!HasProjects</c> alone, so that text does not sit above a populated "Shared via …" section
    /// once one arrives a moment after the window opens (<see cref="LoadSharedProjectsAsync"/> runs in the background).
    /// </summary>
    public bool HasNothingToShow => !HasProjects && !HasSharedProjects;

    /// <summary>True when there are more projects than the sidebar shows, so it can say where the others are.</summary>
    public bool HasMoreThanSidebarShows => Projects.Count > SidebarLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Project? _selectedProject;

    public bool HasSelection => SelectedProject is not null;

    public bool HasProjects => Projects.Count > 0;

    /// <summary>How many projects there are, for the overview's summary line.</summary>
    public int ProjectCount => Projects.Count;

    /// <summary>How many have ever been opened — the rest are set up but never started, which is worth seeing at a glance.</summary>
    public int OpenedProjectCount => Projects.Count(project => project.LastOpenedAt is not null);

    /// <summary>The project a session was last started on, or null when none ever was.</summary>
    public Project? MostRecentProject => RecentProjects.FirstOrDefault(project => project.LastOpenedAt is not null);

    /// <summary>
    /// Records that a session just started on <paramref name="project"/>, so the overview can lead with what is
    /// actually worked on. Persists like every other change here; a project removed in the meantime is left alone
    /// rather than written back.
    /// </summary>
    public async Task MarkOpenedAsync(Project project, DateTimeOffset openedAt)
    {
        if (_settings.Projects.FirstOrDefault(candidate => candidate.Id == project.Id) is not { } stored)
        {
            return;
        }

        await _PersistAsync(_settings.WithUpdated(stored with { LastOpenedAt = openedAt }));
    }

    /// <summary>
    /// A manager holding one sample project, for a headless render of a surface that shows projects — the
    /// parameterless constructor is deliberately empty, and an empty list renders the "no projects yet" state
    /// instead of the rows under test. Mirrors <c>TtyViewModel.DesignTerminal</c>.
    /// </summary>
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

    /// <summary>
    /// <see cref="DesignSample"/> plus shared-project groups (AC-245), staged directly rather than through
    /// <see cref="LoadSharedProjectsAsync"/> — there is no <see cref="ISharedProjectSource"/> or host in a headless
    /// render, the same reason <c>_ProjectEditorWithMemorySourceReachability</c> (Screenshotter) stages its own
    /// state directly instead of going through a real check delegate. One group with two rows (a name/description/
    /// role each), one group carrying an error instead — the two states the workspace actually draws differently.
    /// </summary>
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

    /// <summary>
    /// Categories (AC-618) as the list's main grouping — "Werk" before "Privé", not alphabetical, from an explicit
    /// <see cref="ProjectSettings.CategoryOrder"/> rather than either name's own spelling — with "Uncategorized"
    /// always last, shown even though nothing is in it here, and every card's own origin badge instead of AC-245's
    /// retired "On this machine" heading: "Onboarding flow" carries a real <see cref="ProjectOwnershipRegistry"/>
    /// claim, the same seam AC-604/AC-245 use, so it draws "◆ Depot — Work" rather than "● This machine".
    /// </summary>
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

        return viewModel;
    }

    /// <summary>
    /// Reads the saved projects. Called when Options opens, so an edit made elsewhere is reflected rather than
    /// overwritten. Deliberately does not wait on <see cref="LoadSharedProjectsAsync"/> — it only starts it: the
    /// caller (<c>SessionDialogService.ShowProjectsDialogAsync</c>) awaits this before the workspace window is
    /// even constructed, and a Depot connection that is slow or unreachable must not hold that window closed while
    /// the local projects it already has sit ready.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _Republish();

        _sharedProjectsLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sharedProjectsLoadCts = cts;
        SharedProjectsLoadTask = LoadSharedProjectsAsync(cts.Token);
    }

    /// <summary>
    /// Fills <see cref="SharedProjectGroups"/> from every registered <see cref="ISharedProjectSource"/> (AC-245).
    /// Public (rather than folded into <see cref="LoadAsync"/>) so a test can await it directly instead of racing
    /// the fire-and-forget call <see cref="LoadAsync"/> makes.
    /// <para>
    /// A shared project already bound to a local one (its <see cref="SharedProject.Id"/> matches a
    /// <see cref="ProjectResourceRole.Memory"/> row somewhere in <see cref="ProjectSettings.Projects"/>) is left out
    /// — it already shows under "On this machine" — and, for that local project, has its origin claimed through
    /// <see cref="IProjectOwnershipRegistry"/> (AC-604's own seam) so the editor can draw the ◆ Shared badge on it.
    /// This runs from here rather than from the plugin that contributed the source: a plugin never sees the local
    /// project list (it would have to reference <c>Cockpit.Core</c>, which no plugin project does), while this view
    /// model already loaded both lists a moment ago.
    /// </para>
    /// A shared project hidden on this machine (<see cref="ProjectSettings.HiddenSharedProjectIds"/>) is left out
    /// the same way. One source failing (not signed in, unreachable, timed out) only empties its own group — every
    /// other source's rows are unaffected — and an empty, error-free group is left out entirely rather than shown
    /// as a heading over nothing.
    /// </summary>
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
        foreach (var (source, result) in sources.Zip(results))
        {
            // Claiming runs over every project this source reported, bound or not — a project already bound is
            // exactly the one this claim is for (it is what makes the editor draw the ◆ Shared badge on it), even
            // though it never appears in this source's own group below (it already shows under "On this machine").
            if (result.Succeeded && _ownership is not null)
            {
                _ClaimBoundProjects(result.Projects, source.SourceName);
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

        SharedProjectGroups.Clear();
        foreach (var group in groups)
        {
            SharedProjectGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasSharedProjects));
        OnPropertyChanged(nameof(HasNothingToShow));

        // AC-618: _ClaimBoundProjects above may just have registered an ownership claim this run — a bound
        // project's card must show its "◆ <connection>" badge without the operator having to touch anything else
        // for _Republish to run again.
        _RepublishCategoryGroups();
    }

    /// <summary>Claims every local project bound to one of <paramref name="sharedProjects"/> as owned by <paramref name="sourceName"/> — see <see cref="LoadSharedProjectsAsync"/>'s own remarks.</summary>
    private void _ClaimBoundProjects(IReadOnlyList<SharedProject> sharedProjects, string sourceName)
    {
        if (sharedProjects.Count == 0)
        {
            return;
        }

        var sharedIds = sharedProjects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var project in _settings.Projects)
        {
            var boundTo = project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
            if (boundTo is { Length: > 0 } && sharedIds.Contains(boundTo))
            {
                // A geclaimed field is locked host-side regardless of IsEditable today — there is nowhere yet for
                // an edit to be written back to (AC-247). See ProjectFieldOwnership's own remarks; not setting this
                // true here is deliberate, not an oversight.
                _ownership!.Register(new ProjectOwnershipRegistration(project.Id, new ProjectFieldOwnership(sourceName)));
            }
        }
    }

    /// <summary>
    /// One source's projects, or a timeout failure if it does not answer within <see cref="_SharedProjectSourceTimeout"/>
    /// — and, defensively, a failure if the source throws instead of reporting one through
    /// <see cref="SharedProjectListResult.Failed"/> as its own contract asks. Never throws: a call superseded by a
    /// newer <see cref="LoadSharedProjectsAsync"/> (its <paramref name="cancellationToken"/> cancelled) also lands
    /// here as an (ignored — see that method's own stale-result check) failure rather than an unobserved exception
    /// on this fire-and-forget call.
    /// </summary>
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

    /// <summary>
    /// Opens the editor for <paramref name="project"/> and saves what comes back. Public because the sidebar
    /// (AC-164) edits the project under the pointer rather than a selection — one editing path either way, so a
    /// project edited from the sidebar and one edited from Options are written the same.
    /// </summary>
    public async Task EditAsync(Project project)
    {
        if (_dialogs is null)
        {
            return;
        }

        if (await _dialogs.ShowProjectDialogAsync(project) is { } edited)
        {
            var stored = await _WithStoredLogoAsync(edited);
            await _PersistAsync(_settings.WithUpdated(stored));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);
        }
    }

    /// <summary>
    /// Removes a project after confirming. Sessions already running under it keep running — a project is what a
    /// session started with, not something it holds open, so removing one is not a reason to stop work in flight.
    /// </summary>
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

    /// <summary>
    /// <paramref name="project"/> with its logo as a copy the cockpit owns. The editor hands back whatever the
    /// operator pointed at — a file, a URL, or the path of the copy already stored; this turns the first two into
    /// a copy, leaves the third alone, and drops the stored one when the field was cleared. A source that cannot be
    /// read costs the picture and not the save: a project is not worth less for a logo that would not load.
    /// </summary>
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

    /// <summary>
    /// Rebuilds <see cref="ProjectCategoryGroups"/> (AC-618) from a locally normalized view of <see cref="_settings"/>
    /// — <see cref="ProjectSettings.Normalized"/> is read here rather than trusted to already be reflected in
    /// <see cref="_settings"/> itself (<see cref="_PersistAsync"/> assigns the settings it was handed, not what
    /// <see cref="IProjectStore.SaveAsync"/> actually normalized and wrote), so a category typed for the first time
    /// this save shows its heading immediately instead of one republish cycle late. Reading it this way changes
    /// nothing about <see cref="_settings"/> itself or what gets persisted.
    /// </summary>
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

    private ProjectCardViewModel _ToCard(Project project) => new(project, _OriginBadge(project));

    /// <summary>
    /// "● This machine", or "◆ &lt;connection&gt;" once <see cref="_ownership"/> has a claim on
    /// <paramref name="project"/> (AC-604's own seam, claimed for a bound project by
    /// <see cref="_ClaimBoundProjects"/>) — the per-card replacement for AC-245's "On this machine" heading.
    /// </summary>
    private string _OriginBadge(Project project) =>
        _ownership?.Resolve(project.Id)?.Values.FirstOrDefault(ownership => ownership is not null) is { } claim
            ? $"◆ {claim.SourceName}"
            : "● This machine";

    partial void OnSelectedProjectChanged(Project? value)
    {
        EditProjectCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
    }
}
