using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Creating or editing one project (AC-160): its name, folder, the profile it starts under, how that profile
/// should behave here, whether its sessions isolate, and which MCP servers they see. Edits a copy and hands the
/// result back on Save — the caller owns the list and the persisting, the way the profile editor works.
/// </summary>
public partial class ProjectDialogViewModel : ViewModelBase
{
    private readonly string? _projectId;

    /// <summary>Raised when the dialog is done: the saved project, or null when the operator cancelled.</summary>
    public event Action<Project?>? CloseRequested;

    /// <summary>Raised when the operator picks "Choose…"; the view opens the folder picker and assigns <see cref="SourceDirectory"/>.</summary>
    public event Action? BrowseRequested;

    /// <summary>Raised when the operator picks "Clone…" (AC-90); the host clones and assigns <see cref="SourceDirectory"/>.</summary>
    public event Action? CloneRequested;

    /// <summary>Raised when the operator wants to pick the logo from a file; the view opens the picker and assigns <see cref="LogoSource"/>.</summary>
    public event Action? PickLogoRequested;

    /// <summary>Design-time constructor for the Avalonia previewer.</summary>
    public ProjectDialogViewModel()
    {
        Profiles.Add("personal");
        SelectedProfileLabel = "personal";
        Name = "Cockpit";
    }

    private ProjectDialogViewModel(Project? project)
    {
        _projectId = project?.Id;
        IsEditing = project is not null;

        if (project is null)
        {
            return;
        }

        Name = project.Name;
        Description = project.Description ?? string.Empty;
        SourceDirectory = project.SourceDirectory ?? string.Empty;
        GitUrl = project.GitUrl;
        BehaviorPrompt = project.BehaviorPrompt ?? string.Empty;
        LogoSource = project.LogoPath ?? string.Empty;
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault;
        MemoryRef = project.MemoryRef;
        _additionalServers = project.McpOverlay.AdditionalServers;
    }

    /// <summary>
    /// A view model for <paramref name="project"/>, or for a new project when it is null, with the profile picker
    /// and MCP checklist filled from the stores. An async factory rather than loading in the constructor, because
    /// both lists come off disk and a half-populated dialog is worse than one that opens a moment later.
    /// </summary>
    public static async Task<ProjectDialogViewModel> CreateAsync(
        Project? project,
        ISessionProfileStore profileStore,
        IMcpServerCatalog mcpServerCatalog,
        CancellationToken cancellationToken = default)
    {
        var viewModel = new ProjectDialogViewModel(project);

        foreach (var profile in await profileStore.LoadAsync(cancellationToken).ConfigureAwait(false))
        {
            viewModel.Profiles.Add(profile.Label);
        }

        viewModel.SelectedProfileLabel = viewModel.Profiles.FirstOrDefault(label =>
            string.Equals(label, project?.DefaultProfileLabel, StringComparison.OrdinalIgnoreCase));

        var servers = await mcpServerCatalog.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var disabled = project?.McpOverlay.DisabledServerNames.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var offered = McpServerRegistryFilter.OfferedToOperator(servers);

        foreach (var server in offered)
        {
            viewModel.McpServers.Add(new McpServerSelectionItemViewModel(server.Name)
            {
                IsEnabledForSession = !disabled.Contains(server.Name),
            });
        }

        // A name this project switched off that the checklist cannot show — the server was disabled in the registry
        // since, or removed — is kept rather than dropped on save, the way the project's own servers are. Editing
        // which servers are on must not silently switch one back on because the row for it was not there.
        viewModel._carriedDisabledServerNames =
            [.. disabled.Where(name => !offered.Any(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))];

        return viewModel;
    }

    /// <summary>The project's own servers, carried through untouched: v1 edits which servers are on, not the servers themselves (see <see cref="ToProject"/>).</summary>
    private readonly IReadOnlyList<McpServerConfig> _additionalServers = [];

    /// <summary>The names this project switched off that the checklist has no row for, carried through so saving cannot switch them back on.</summary>
    private IReadOnlyList<string> _carriedDisabledServerNames = [];

    /// <summary>Whether this is an existing project rather than a new one — drives the title and the confirm button.</summary>
    public bool IsEditing { get; }

    public string DialogTitle => IsEditing ? "Edit project" : "New project";

    public string ConfirmLabel => IsEditing ? "Save" : "Create project";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>
    /// The project's logo as the operator gave it: a file path, an <c>http(s)</c> URL, or the stored copy's path for
    /// one already set. Blank means none — and, on save, means removing the one it had.
    /// </summary>
    [ObservableProperty]
    private string _logoSource = string.Empty;

    [ObservableProperty]
    private string _sourceDirectory = string.Empty;

    [ObservableProperty]
    private string _behaviorPrompt = string.Empty;

    [ObservableProperty]
    private bool _isolateInWorktreeByDefault;

    [ObservableProperty]
    private string? _selectedProfileLabel;

    /// <summary>Where <see cref="SourceDirectory"/> was cloned from, kept so an edit does not lose it. Set by the clone flow, never typed.</summary>
    public string? GitUrl { get; private set; }

    /// <summary>The knowledge store reference (AC-166), carried through so editing a project in v1 does not drop what v2 wrote.</summary>
    public string? MemoryRef { get; }

    /// <summary>The configured profiles, by label — a project points at one, it does not own one.</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    /// <summary>Every offered MCP server with whether this project's sessions get it. Unticking one is what fills the overlay's disabled list.</summary>
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    public bool HasMcpServers => McpServers.Count > 0;

    /// <summary>A project needs a name — it is what every other surface shows it by.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    /// <summary>Assigns a folder chosen by the picker, dropping a stale clone URL when the operator points the project somewhere else.</summary>
    public void ApplyPickedDirectory(string directory, string? gitUrl = null)
    {
        SourceDirectory = directory;
        GitUrl = gitUrl;
    }

    /// <summary>The edited values as a project, reusing the id when editing so the sessions and settings that reference it keep pointing at the same one.</summary>
    public Project ToProject() =>
        new(_projectId ?? Guid.NewGuid().ToString("n"), Name.Trim())
        {
            Description = _NullIfBlank(Description),
            SourceDirectory = _NullIfBlank(SourceDirectory),
            GitUrl = GitUrl,
            DefaultProfileLabel = SelectedProfileLabel,
            BehaviorPrompt = _NullIfBlank(BehaviorPrompt),
            // What the operator pointed at — a file, a URL, or the stored copy's path when they left it alone. The
            // manager turns it into a copy the cockpit owns; the editor only carries the answer, as it does the rest.
            LogoPath = _NullIfBlank(LogoSource),
            IsolateInWorktreeByDefault = IsolateInWorktreeByDefault,
            MemoryRef = MemoryRef,
            McpOverlay = new ProjectMcpOverlay
            {
                DisabledServerNames =
                [
                    .. McpServers.Where(server => !server.IsEnabledForSession).Select(server => server.Name),
                    .. _carriedDisabledServerNames,
                ],
                AdditionalServers = _additionalServers,
            },
        };

    [RelayCommand]
    private void Browse() => BrowseRequested?.Invoke();

    [RelayCommand]
    private void PickLogo() => PickLogoRequested?.Invoke();

    /// <summary>Drops the logo. The stored copy goes when the project is saved, not here — cancelling must leave it as it was.</summary>
    [RelayCommand]
    private void ClearLogo() => LogoSource = string.Empty;

    [RelayCommand]
    private void Clone() => CloneRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(ToProject());

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    private static string? _NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
