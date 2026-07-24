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

    public ProjectsViewModel(IProjectStore store, ISessionDialogService? dialogs)
    {
        _store = store;
        _dialogs = dialogs;
    }

    /// <summary>The saved projects, in the order the manager and the launcher show them.</summary>
    public ObservableCollection<Project> Projects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private Project? _selectedProject;

    public bool HasSelection => SelectedProject is not null;

    public bool HasProjects => Projects.Count > 0;

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
            await _PersistAsync(_settings.WithProject(created));
            SelectedProject = Projects.FirstOrDefault(project => project.Id == created.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditProjectAsync()
    {
        if (_dialogs is null || SelectedProject is not { } project)
        {
            return;
        }

        if (await _dialogs.ShowProjectDialogAsync(project) is { } edited)
        {
            await _PersistAsync(_settings.WithUpdated(edited));
            SelectedProject = Projects.FirstOrDefault(candidate => candidate.Id == edited.Id);
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
            await _PersistAsync(_settings.WithoutProject(project.Id));
        }
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

        SelectedProject = Projects.FirstOrDefault(project => project.Id == selectedId);
        OnPropertyChanged(nameof(HasProjects));
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        EditProjectCommand.NotifyCanExecuteChanged();
        RemoveProjectCommand.NotifyCanExecuteChanged();
    }
}
