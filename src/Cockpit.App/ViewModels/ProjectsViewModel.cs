using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The projects manager behind Options → Projects (AC-161): the saved projects, and add/edit/remove over them.
/// Owns the persisting that <see cref="ProjectDialogViewModel"/> deliberately does not, so the editor stays a
/// value editor and this is the only thing that writes the list.
/// </summary>
public partial class ProjectsViewModel : ViewModelBase, ISingletonService
{
    private readonly IProjectStore _store;

    /// <summary>Takes the cockpit's own copy of a picked or downloaded logo. Null under the previewer, where a project keeps whatever path it was given.</summary>
    private readonly IProjectLogoStore? _logos;

    /// <summary>Null only under the previewer, which has no window to open a dialog over; every command that needs one is inert there.</summary>
    private readonly ISessionDialogService? _dialogs;

    private ProjectSettings _settings = ProjectSettings.Empty;

    /// <summary>
    /// Design-time constructor for the Avalonia previewer: an empty store and no dialog service, so a rendered
    /// surface can reach neither the operator's config nor a window that does not exist there. The commands are
    /// inert in that context — see <see cref="_dialogs"/>.
    /// </summary>
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

    /// <summary>The saved projects in the order they are stored — what the manager lists and edits.</summary>
    public ObservableCollection<Project> Projects { get; } = [];

    /// <summary>
    /// The same projects, most recently opened first and never-opened ones after them by name — what the overview
    /// leads with. A separate list rather than a re-sorted <see cref="Projects"/>: the manager's order is the
    /// operator's own, and re-ordering it under them every time a session starts would be its own small chaos.
    /// </summary>
    public ObservableCollection<Project> RecentProjects { get; } = [];

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

    /// <summary>Reads the saved projects. Called when Options opens, so an edit made elsewhere is reflected rather than overwritten.</summary>
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

        SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedId);
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(ProjectCount));
        OnPropertyChanged(nameof(OpenedProjectCount));
        OnPropertyChanged(nameof(MostRecentProject));
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        EditProjectCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
    }
}
