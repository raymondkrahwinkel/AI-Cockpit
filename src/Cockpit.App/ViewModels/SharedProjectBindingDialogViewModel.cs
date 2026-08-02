using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// The "Finish setting up…" bind step (AC-246): the one-time bind step that turns a `SharedProject`
// this machine has not bound yet into an ordinary local `Project`. Deliberately its own, much smaller dialog
// rather than `ProjectDialogViewModel` with everything locked (AC-242 mockup, section 4) — the two
// answer different questions: this one asks only what is machine-local and has never been set before (a project
// with ten shared resource rows would otherwise open its very first screen as the same long form
// `ProjectDialogViewModel` grows into over time), while the full editor (already reachable afterward,
// once `ProjectsViewModel._ClaimBoundProjects` has claimed the result) is where the shared fields are shown,
// locked, with their ◆/● origin badges.
//
// AC-246 decision (Raymond, 2026-08-02): only `SelectedProfileLabel` is required. `SourceDirectory`
// is an offer, not a gate — a shared project with no `GitUrl` and no files of its own (a notes-only
// project) binds and starts with a profile alone. Never filled in automatically: an empty folder is not this
// dialog's problem to solve on the operator's behalf.
//
// AC-246: every machine-specific reference is asked here, once, rather than synchronized (Raymond, 2026-08-02:
// "dat paden stukje lokaal is voor een project") — see `ResourceRows`. A machine-scope row travels as
// a *placeholder*: `Cockpit.Plugin.Depot.ProjectDefinition.CockpitProjectResourceEntry.Create` (AC-246,
// 2026-08-02) still keeps a secret-shaped reference (AC-612) out entirely, but a plain absolute path now writes
// role and label with the reference itself left blank, so this dialog has something to ask about — a real project
// with an absolute resource row is the normal case this block exists for now, not only a hand-edited or
// future-writer defence. `ResourceRows` is populated from the caller's own read of the definition,
// judged fresh here rather than trusted on the writer's word either way: a blank reference on a row this dialog
// did not expect (a hand-edited `.cockpit/project.json`, a row this build's role parser does not recognise)
// is treated the same as an ordinary placeholder, and a non-blank absolute reference (an older writer that never
// learned the placeholder shape) still asks about it rather than trusting it.
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

    // A design/screenshot instance with machine-specific rows to ask about (AC-246's own vormwaarschuwing: this
    // block, not Profile/Folder, is the one that can grow) — three rows rather than the bare scene above, enough
    // to prove the bounded, independently scrollable block actually scrolls instead of pushing Profile/Folder off
    // the window. Two placeholder rows (no `OriginalReference` — the normal case now that
    // `CockpitProjectResourceEntry.Create` writes a machine-scope row as role + label with the reference
    // withheld) and one defensive row that still shows a "was: …" hint, so the scene proves both shapes render.
    // Mirrors `ProjectsViewModel.DesignSampleWithSharedProjects`'s own "stage it directly, there is no host
    // or plugin in a headless render" reasoning.
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

    private SharedProjectBindingDialogViewModel(SharedProject sharedProject, string sourceName, SharedProjectBinding binding)
    {
        _sharedProjectId = sharedProject.Id;
        SourceName = sourceName;
        ProjectName = binding.Name;
        Description = binding.Description;
        GitUrl = binding.GitUrl;
        _behaviorPrompt = binding.BehaviorPrompt;
        _isolateInWorktreeByDefault = binding.IsolateInWorktreeByDefault;
        _enabledMcpServerNames = binding.EnabledMcpServerNames;

        foreach (var resource in binding.Resources)
        {
            var role = Enum.TryParse<ProjectResourceRole>(resource.Role, ignoreCase: true, out var parsed)
                ? parsed
                : ProjectResourceRole.Reference; // Unrecognised role: fall back to the least powerful one (looked up, never obeyed or written).

            // AC-246 (Raymond, 2026-08-02): a blank Reference here is a placeholder, not "nothing to name" — see
            // SharedProjectBindingResource.Reference's own remarks. ClassifyScope itself answers null for a blank
            // reference (nothing to judge), which would otherwise fall into the "already portable" branch below and
            // silently add a resource row that names nothing at all — exactly the row this ticket exists to ask
            // about instead. A non-blank Machine-scope reference is still handled the same way: the purely
            // defensive path from before this ticket, for a row this reader cannot trust the writer's own gate to
            // have caught (a hand-edited definition, say).
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

    // Builds the dialog for `sharedProject`, reading its full definition through
    // `source` first (AC-246) — `ISharedProjectSource.ListAsync`'s own read only ever
    // kept a name, description and role, not enough to bind. Null when the read failed; the caller (the dialog
    // service) is what shows the error, the same split `ProjectDialogViewModel.CreateAsync` leaves to its own
    // caller for a failure of its own.
    public static async Task<(SharedProjectBindingDialogViewModel? ViewModel, string? Error)> CreateAsync(
        SharedProject sharedProject,
        string sourceName,
        ISharedProjectSource source,
        ISessionProfileStore profileStore,
        CancellationToken cancellationToken = default)
    {
        var result = await source.PrepareBindingAsync(sharedProject.Id, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Binding is not { } binding)
        {
            return (null, result.Error is { Length: > 0 } error ? error : "Could not read this project's definition.");
        }

        var viewModel = new SharedProjectBindingDialogViewModel(sharedProject, sourceName, binding);

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

    // Behind `GitUrl` so "Clone…" builds on it; carried through untouched to the new project.
    private readonly string? _behaviorPrompt;

    private readonly bool _isolateInWorktreeByDefault;

    private readonly IReadOnlyList<string>? _enabledMcpServerNames;

    // Resource rows already portable by shape — copied straight onto the new project, never shown or asked about here (AC-605's table: nothing to ask when it already travels).
    private readonly List<ProjectResource> _portableResources = [];

    public string DialogTitle => $"Finish setting up — {ProjectName}";

    public string Hint => $"This project comes from {SourceName}. Name, behaviour, MCP choice and memory are already set up — fill in what is yours.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? _selectedProfileLabel;

    [ObservableProperty]
    private string _sourceDirectory = string.Empty;

    // Where `SourceDirectory` can be cloned from — an offer (AC-246), never required. Null for a project with no source of its own.
    public string? GitUrl { get; private set; }

    public bool HasGitUrl => !string.IsNullOrWhiteSpace(GitUrl);

    // The configured profiles, by label — the one required field.
    public ObservableCollection<string> Profiles { get; } = [];

    // Machine-specific rows the shared definition names but does not carry a value for (AC-246, AC-605: only a
    // `ProjectResourceScope.Machine` reference lands here — Repo/Home/Instance rows already travel and
    // were folded straight into the new project instead). Almost always empty in practice — see this class's own
    // remarks on why the writer never lets one reach here to begin with. Skippable: a row left blank is dropped on
    // save, the same as any other blank row in this codebase's project editors.
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

    // The bound project. The binding itself is a `ProjectResourceRole.Memory` row whose reference is
    // `SharedProject.Id`, prepended ahead of everything else (AC-246) — the same shape
    // `ProjectsViewModel.LoadSharedProjectsAsync`/`_ClaimBoundProjects` already read to recognise a bound
    // project and claim its shared fields' origin (AC-604/AC-245), and the same mechanism the AC-242 mockup names:
    // one Depot project carries both the memory and the definition. Prepended rather than appended so it is always
    // what `Project.MemoryRef` resolves to, even if a hand-edited definition somehow also carried its own
    // Memory-role row.
    public Project ToProject()
    {
        var overlay = _enabledMcpServerNames is { } enabled
            ? new ProjectMcpOverlay { EnabledServerNames = enabled }
            : ProjectMcpOverlay.None;

        return new Project(Guid.NewGuid().ToString("n"), ProjectName)
        {
            Description = Description,
            SourceDirectory = _NullIfBlank(SourceDirectory),
            GitUrl = GitUrl,
            DefaultProfileLabel = SelectedProfileLabel,
            BehaviorPrompt = _behaviorPrompt,
            IsolateInWorktreeByDefault = _isolateInWorktreeByDefault,
            McpOverlay = overlay,
            Resources =
            [
                new ProjectResource(_sharedProjectId, ProjectResourceRole.Memory),
                .. _portableResources,
                .. ResourceRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.Reference))
                    .Select(row => new ProjectResource(row.Reference.Trim(), row.Role) { Label = row.Label }),
            ],
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
