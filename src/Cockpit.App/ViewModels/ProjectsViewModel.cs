using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// The projects manager behind Options → Projects (AC-161): the saved projects, and add/edit/remove over them.
// Owns the persisting that `ProjectDialogViewModel` deliberately does not, so the editor stays a
// value editor and this is the only thing that writes the list.
public partial class ProjectsViewModel : ViewModelBase, ISingletonService
{
    private readonly IProjectStore _store;

    // Takes the cockpit's own copy of a picked or downloaded logo. Null under the previewer, where a project keeps whatever path it was given.
    private readonly IProjectLogoStore? _logos;

    // Null only under the previewer, which has no window to open a dialog over; every command that needs one is inert there.
    private readonly ISessionDialogService? _dialogs;

    private ProjectSettings _settings = ProjectSettings.Empty;

    // Design-time constructor for the Avalonia previewer: an empty store and no dialog service, so a rendered
    // surface can reach neither the operator's config nor a window that does not exist there. The commands are
    // inert in that context — see `_dialogs`.
    public ProjectsViewModel()
        : this(new DesignTimeProjectStore(), dialogs: null)
    {
    }

    public ProjectsViewModel(IProjectStore store, ISessionDialogService? dialogs, IProjectLogoStore? logos = null)
    {
        _store = store;
        _dialogs = dialogs;
        _logos = logos;
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

    // Reads the saved projects. Called when Options opens, so an edit made elsewhere is reflected rather than overwritten.
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _Republish();
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

        if (await _dialogs.ShowProjectDialogAsync(project) is { } edited)
        {
            var stored = await _WithStoredLogoAsync(edited);
            await _PersistAsync(_settings.WithUpdated(stored));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == stored.Id);
        }
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
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(OpenedProjectCount));
        OnPropertyChanged(nameof(MostRecentProject));
        OnPropertyChanged(nameof(HasMoreThanSidebarShows));
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        EditProjectCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
    }
}
