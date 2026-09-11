using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Plugins;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Backs the quick-note surface (AC-492): one note, one project, saved into that project's memory without a
// session. "Save" writes and nothing else; "Save and start" is the second, separate action. A write that
// fails keeps the note where it is — the operator never loses what they typed.
public sealed partial class QuickNoteViewModel : ObservableObject
{
    private readonly IProjectMemoryNoteWriter? _writer;
    private readonly Func<Project, string, Task>? _start;

    // Design-time: two sample destinations, the first one picked.
    public QuickNoteViewModel()
        : this(
            [
                new Project("p1", "Onboarding") { MemoryRef = "depot:onboarding" },
                new Project("p2", "Cockpit") { MemoryRef = "depot:cockpit" },
            ],
            writer: null,
            start: null)
    {
    }

    // `projects` is offered as given: the caller has already filtered to what can be written to and ordered it
    // most-recently-opened first (`ProjectsViewModel.RecentProjects`), so the first one is the default pick.
    public QuickNoteViewModel(IReadOnlyList<Project> projects, IProjectMemoryNoteWriter? writer, Func<Project, string, Task>? start)
    {
        Projects = projects;
        _writer = writer;
        _start = start;
        SelectedProject = projects.FirstOrDefault();
    }

    public IReadOnlyList<Project> Projects { get; }

    public bool HasProjects => Projects.Count > 0;

    [ObservableProperty]
    private Project? _selectedProject;

    [ObservableProperty]
    private string _note = string.Empty;

    // Why the last save did not land, or empty. Shown under the note, which stays put.
    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isSaving;

    // Raised once the note is in the project's memory (and, for "save and start", the session is on its way) — the
    // window closes on it. Never raised on a failed write. `Note` is empty by then: what landed is no longer a draft.
    public event EventHandler? Saved;

    [RelayCommand]
    private Task SaveAsync() => _SaveAsync(start: false);

    [RelayCommand]
    private Task SaveAndStartAsync() => _SaveAsync(start: true);

    private async Task _SaveAsync(bool start)
    {
        if (_writer is null || SelectedProject is not { } project)
        {
            Message = "There is no project to save this into.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Note))
        {
            Message = "Type something first.";
            return;
        }

        IsSaving = true;
        try
        {
            var result = await _writer.AppendAsync(project, Note, CancellationToken.None);
            Message = result.Outcome switch
            {
                ProjectMemoryAppendOutcome.Success => string.Empty,
                ProjectMemoryAppendOutcome.AuthorizationRequired =>
                    $"{project.Name}'s memory needs a sign-in first — sign in, then save again. Your note is kept here.",
                _ => $"Could not save: {result.Error ?? "unknown error"}. Your note is kept here.",
            };

            if (result.Outcome != ProjectMemoryAppendOutcome.Success)
            {
                return;
            }

            if (start && _start is { } startSession)
            {
                await startSession(project, Note);
            }

            Note = string.Empty;
            Saved?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
