using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Deliberately its own, much smaller dialog rather than `ProjectDialogViewModel` with everything locked (AC-242 mockup,
// section 4) (AC-246, AC-612).
public partial class SharedProjectBindingDialogViewModel : ViewModelBase
{
    private readonly string _sharedProjectId;

    // Raised when the dialog is done: the new project, or null when the operator cancelled.
    public event Action<Project?>? CloseRequested;

    // Raised when the operator picks "Choose…"; the view opens the folder picker and assigns `SourceDirectory`.
    public event Action? BrowseRequested;

    // Raised when the operator picks "Clone…"; the host clones `GitUrl` and assigns `SourceDirectory`.
    public event Action? CloneRequested;

    // Design-time constructor for the Avalonia previewer.
    public SharedProjectBindingDialogViewModel()
    {
        _sharedProjectId = "depot:handbook";
        SourceName = "Work";
        ProjectName = "Handbook";
        GitUrl = "git@github.com:example/handbook.git";
        Profiles.Add("Zyra — Sonnet");
    }

    // A design/screenshot instance with machine-specific rows to ask about (AC-246's own vormwaarschuwing: this block,
    // not Profile/Folder, is the one that can grow).
    internal static SharedProjectBindingDialogViewModel DesignSampleWithResourceRows()
    {
        var viewModel = new SharedProjectBindingDialogViewModel();

        viewModel.ResourceRows.Add(new SharedProjectBindingResourceRowViewModel(
            ProjectResourceRole.Instructions, "Onboarding runbook", originalReference: null));
        viewModel.ResourceRows.Add(new SharedProjectBindingResourceRowViewModel(
            ProjectResourceRole.Reference, "Test dataset", originalReference: null));
        viewModel.ResourceRows.Add(new SharedProjectBindingResourceRowViewModel(
            ProjectResourceRole.Reference, null, "/home/erik/work/handbook/scratch"));

        return viewModel;
    }

    private SharedProjectBindingDialogViewModel(string sharedProjectId, string sourceName, SharedProjectBinding binding)
    {
        _sharedProjectId = sharedProjectId;
        SourceName = sourceName;
        ProjectName = binding.Name;
        Description = binding.Description;
        GitUrl = binding.GitUrl;
        _behaviorPrompt = binding.BehaviorPrompt;
        _isolateInWorktreeByDefault = binding.IsolateInWorktreeByDefault;
        _enabledMcpServerNames = binding.EnabledMcpServerNames;
        _logoBytes = binding.LogoBytes;

        foreach (var resource in binding.Resources)
        {
            var role = Enum.TryParse<ProjectResourceRole>(resource.Role, ignoreCase: true, out var parsed)
                ? parsed
                : ProjectResourceRole.Reference; // Unrecognised role: fall back to the least powerful one (looked up, never obeyed or written).

            // ClassifyScope itself answers null for a blank reference (nothing to judge), which would otherwise fall
            // into the "already portable" branch below and silently add a resource row that names nothing at all —
            // exactly the row this ticket exists to ask about instead (AC-246).
            var isPlaceholder = string.IsNullOrWhiteSpace(resource.Reference);
            if (isPlaceholder || ProjectResourcePathPortability.ClassifyScope(resource.Reference) == ProjectResourceScope.Machine)
            {
                ResourceRows.Add(new SharedProjectBindingResourceRowViewModel(role, resource.Label, isPlaceholder ? null : resource.Reference));
            }
            else
            {
                // Already portable (Repo/Home/Instance) — AC-605's table says this travels on its own; nothing to ask.
                _portableResources.Add(new ProjectResource(resource.Reference, role) { Label = resource.Label });
            }
        }
    }

    // Builds the dialog for `sharedProjectId`, reading its full definition through `source` first (AC-246) —
    // `ISharedProjectSource.ListAsync`'s own read only ever kept a name, description and role, not enough to bind
    // (AC-798).
    public static async Task<(SharedProjectBindingDialogViewModel? ViewModel, string? Error)> CreateAsync(
        string sharedProjectId,
        string sourceName,
        ISharedProjectSource source,
        ISessionProfileStore profileStore,
        CancellationToken cancellationToken = default)
    {
        var result = await source.PrepareBindingAsync(sharedProjectId, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Binding is not { } binding)
        {
            return (null, result.Error is { Length: > 0 } error ? error : "Could not read this project's definition.");
        }

        var viewModel = new SharedProjectBindingDialogViewModel(sharedProjectId, sourceName, binding);

        foreach (var profile in await profileStore.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            viewModel.Profiles.Add(profile.Label);
        }

        return (viewModel, null);
    }

    // Which connection this project comes from — "Work", "Personal" — shown in the hint line above the fields.
    public string SourceName { get; }

    // The shared project's own name, read live at open time — shown in the title, never edited here (AC-246: name is claimed, not asked).
    public string ProjectName { get; }

    public string? Description { get; }

    // AC-1071: shown and editable here rather than carried through unseen — whoever binds a shared project now
    // sees the standing instruction they are taking on, and can change it before it ever reaches their own
    // config. The persona that used to hide in here is its own field now, and never travels at all.
    [ObservableProperty]
    private string? _behaviorPrompt;

    private readonly bool _isolateInWorktreeByDefault;

    private readonly IReadOnlyList<string>? _enabledMcpServerNames;

    // The shared logo's own bytes (AC-763), already downloaded by PrepareBindingAsync — null when the source has
    // none, or the download itself failed. See ToProject's own remarks on why this becomes a temp file.
    private readonly byte[]? _logoBytes;

    // Resource rows already portable by shape — copied straight onto the new project, never shown or asked about here (AC-605's table: nothing to ask when it already travels).
    private readonly List<ProjectResource> _portableResources = [];

    // Named after the button that opens it (AC-772): the card says "Add to my projects…", so a window titled
    // "Finish setting up" would read as having landed somewhere else.
    public string DialogTitle => $"Add to my projects — {ProjectName}";

    public string Hint => $"This project comes from {SourceName}. Name, MCP choice and memory are already set up — check what it asks of a session, and fill in what is yours.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _selectedProfileLabel;

    [ObservableProperty]
    private string _sourceDirectory = string.Empty;

    // Where `SourceDirectory` can be cloned from — an offer (AC-246), never required. Null for a project with no source of its own.
    public string? GitUrl { get; private set; }

    public bool HasGitUrl => !string.IsNullOrWhiteSpace(GitUrl);

    // The configured profiles, by label — the one required field.
    public ObservableCollection<string> Profiles { get; } = [];

    // Machine-specific rows the shared definition names but does not carry a value for (AC-246, AC-605: only a
    // `ProjectResourceScope.Machine` reference lands here — Repo/Home/Instance rows already travel and were folded
    // straight into the new project instead).
    public ObservableCollection<SharedProjectBindingResourceRowViewModel> ResourceRows { get; } = [];

    public bool HasResourceRows => ResourceRows.Count > 0;

    // Only a profile is required (AC-246 decision, 2026-08-02) — the folder is an offer, not a gate.
    public bool CanSave => !string.IsNullOrWhiteSpace(SelectedProfileLabel);

    // Assigns a folder chosen by the picker, dropping a stale clone URL when the operator points the project somewhere else — mirrors `ProjectDialogViewModel.ApplyPickedDirectory`.
    public void ApplyPickedDirectory(string directory, string? gitUrl = null)
    {
        SourceDirectory = directory;
        GitUrl = gitUrl;
        OnPropertyChanged(nameof(GitUrl));
        OnPropertyChanged(nameof(HasGitUrl));
    }

    // Prepended rather than appended so it is always what `Project.MemoryRef` resolves to, even if a hand-edited
    // definition somehow also carried its own Memory-role row (AC-246, AC-604, AC-245, AC-242).
    public Project ToProject()
    {
        var overlay = _enabledMcpServerNames is { } enabled
            ? new ProjectMcpOverlay { EnabledServerNames = enabled }
            : ProjectMcpOverlay.None;

        return new Project(Guid.NewGuid().ToString("n"), ProjectName)
        {
            Description = Description,
            // Single-repository only (AC-938 non-goal): a shared project definition never carries more than the
            // one machine-local folder this dialog offers — see this class's own remarks on why SourceDirectory
            // travels with a bound project at all.
            SourceDirectories = _NullIfBlank(SourceDirectory) is { } folder ? [new(folder)] : [],
            GitUrl = GitUrl,
            DefaultProfileLabel = SelectedProfileLabel,
            BehaviorPrompt = string.IsNullOrWhiteSpace(BehaviorPrompt) ? null : BehaviorPrompt.Trim(),
            // AC-1071: deliberately not taken from the shared definition — the assistant is this operator's own
            // answer, and a bound project that set one would be exactly the imposition this ticket removes.
            Assistant = null,
            IsolateInWorktreeByDefault = _isolateInWorktreeByDefault,
            LogoPath = TempLogoFile.WriteOrNull(_logoBytes),
            McpOverlay = overlay,
            Resources =
            [
                new ProjectResource(_sharedProjectId, ProjectResourceRole.Memory),
                .. _portableResources,
                .. ResourceRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.Reference))
                    .Select(row => new ProjectResource(row.Reference.Trim(), row.Role) { Label = row.Label }),
            ],
            // AC-762: the ◆ badge's fallback for a cold start — see Project.SharedSourceName.
            SharedSourceName = SourceName,
        };
    }

    [RelayCommand]
    private void Browse() => BrowseRequested?.Invoke();

    [RelayCommand]
    private void Clone() => CloneRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(ToProject());

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    private static string? _NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// One machine-specific row `SharedProjectBindingDialogViewModel.ResourceRows` asks about (AC-246) — the
// role and label came from the shared definition; only `Reference` is this machine's to fill in.
public sealed partial class SharedProjectBindingResourceRowViewModel : ViewModelBase
{
    public SharedProjectBindingResourceRowViewModel(ProjectResourceRole role, string? label, string? originalReference)
    {
        Role = role;
        Label = label;
        OriginalReference = originalReference;
    }

    internal ProjectResourceRole Role { get; }

    public string? Label { get; }

    // What the shared definition's row displays when there is no `Label` to show instead — the role and, when the writer's own machine had one, the reference that will not travel.
    public string DisplayLabel => Label is { Length: > 0 } ? Label : $"{Role} reference";

    // The absolute reference exactly as the shared definition carried it — shown as context only ("was: …"),
    // never copied into `Reference`: it names a place only the machine that wrote it has.
    public string? OriginalReference { get; }

    [ObservableProperty]
    private string _reference = string.Empty;
}
