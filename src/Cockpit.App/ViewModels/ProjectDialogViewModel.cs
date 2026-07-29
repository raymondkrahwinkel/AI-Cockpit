using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

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

    /// <summary>Raised when the operator wants to pick the memory folder; the view opens the folder picker and assigns <see cref="MemoryRef"/>.</summary>
    public event Action? PickMemoryRequested;

    /// <summary>Design-time constructor for the Avalonia previewer.</summary>
    public ProjectDialogViewModel()
    {
        Profiles.Add("personal");
        SelectedProfileLabel = "personal";
        Name = "Cockpit";
        AdditionalInfo.Add(new ProjectInfoFieldViewModel("Repository", "https://github.com/example/ai-cockpit", isSharedWithSessions: true));
        AdditionalInfo.Add(new ProjectInfoFieldViewModel("Customer", "Acme BV — ask for their project lead"));
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
        MemoryRef = project.MemoryRef ?? string.Empty;
        _additionalServers = project.McpOverlay.AdditionalServers;
        _carriedResources = project.Resources;

        foreach (var field in project.AdditionalInfo)
        {
            AdditionalInfo.Add(new ProjectInfoFieldViewModel(field.Label, field.Value, field.IsSharedWithSessions, field.IsSecret));
        }
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
        IReadOnlyList<ProjectFieldRegistration>? pluginFields = null,
        IReadOnlyList<ProjectMemorySourceRegistration>? memorySources = null,
        CancellationToken cancellationToken = default)
    {
        var viewModel = new ProjectDialogViewModel(project);

        foreach (var registration in pluginFields ?? [])
        {
            viewModel.PluginFields.Add(new ProjectPluginFieldViewModel(registration, project?.LinkedAs(registration.Key)));
        }

        // Nothing registered leaves the picker out entirely (HasMemorySources) — the row keeps looking and behaving
        // exactly as it did before AC-166 existed, which is the default this feature must not shift.
        foreach (var registration in memorySources ?? [])
        {
            if (viewModel.MemorySourceChoices.Count == 0)
            {
                viewModel.MemorySourceChoices.Add(new MemorySourceChoice("Folder", Scheme: null));
            }

            viewModel.MemorySourceChoices.Add(new MemorySourceChoice(registration.Title, registration.Scheme));
        }

        // Folder is the default selection the instant there is a picker at all — a plain path, a reference naming
        // no installed source, or (the everyday case) a brand-new project with no MemoryRef yet. The match below
        // overwrites this only when the stored reference actually names a registered source; every other case
        // leaves Folder selected, which is what the ComboBox must show rather than nothing at all.
        if (viewModel.MemorySourceChoices.Count > 0)
        {
            viewModel.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[0];
        }

        // A saved reference of the shape "<scheme>:<value>" naming a source actually offered here selects that
        // source and shows the bare value; anything else — a path, a scheme no installed plugin registered, an
        // empty value after the colon — leaves "Folder" selected (set above) and MemoryRef exactly as the project
        // stored it (set in the constructor above). That is deliberate, not merely the fallback case: a plugin that is
        // temporarily uninstalled must not lose or garble the reference just because this dialog was opened and
        // saved while it was gone.
        if (viewModel.MemorySourceChoices.Count > 0
            && ProjectMemoryRef.TryParse(project?.MemoryRef, out var scheme, out var value)
            && viewModel.MemorySourceChoices.FirstOrDefault(choice =>
                choice.Scheme is { } candidate && string.Equals(candidate, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched)
        {
            viewModel.SelectedMemorySourceChoice = matched;
            viewModel.MemoryRef = value;
        }

        // A link under a key no installed plugin claims — the plugin was removed, or is simply not on this machine —
        // is carried through rather than dropped on save, the way a disabled server name with no row is. Uninstalling
        // a plugin must not quietly unlink every project that used it.
        viewModel._carriedPluginFields = project?.PluginFields
            .Where(link => !viewModel.PluginFields.Any(field => string.Equals(field.Key, link.Key, StringComparison.Ordinal)))
            .ToDictionary(link => link.Key, link => link.Value, StringComparer.Ordinal)
            ?? [];

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

    /// <summary>
    /// The project's resources (AC-483) as they were opened, carried through untouched — this dialog edits only the
    /// single memory value in <see cref="MemoryRef"/>, so an Instructions or Reference row (or a second Memory row
    /// this v1 UI has no box for) must survive the round trip the same way <see cref="_additionalServers"/> and
    /// <see cref="_carriedPluginFields"/> already do. <see cref="ToProject"/> sets this <em>before</em> folding
    /// <see cref="MemoryRef"/> in, because <see cref="Project.MemoryRef"/>'s own doc comment warns that whichever of
    /// the two an initializer sets last wins the same underlying list.
    /// </summary>
    private readonly IReadOnlyList<ProjectResource> _carriedResources = [];

    /// <summary>The names this project switched off that the checklist has no row for, carried through so saving cannot switch them back on.</summary>
    private IReadOnlyList<string> _carriedDisabledServerNames = [];

    /// <summary>The links this project holds under keys no installed plugin registered, carried through so saving cannot drop them.</summary>
    private IReadOnlyDictionary<string, string> _carriedPluginFields = ReadOnlyDictionary<string, string>.Empty;

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

    /// <summary>
    /// Where this project's memory lives: the folder's path in "Folder" mode, or the bare identifier
    /// (<c>cockpit</c>, not <c>depot:cockpit</c>) once <see cref="SelectedMemorySourceChoice"/> names a source
    /// (AC-165/166). Blank for a project that keeps none.
    /// </summary>
    [ObservableProperty]
    private string _memoryRef = string.Empty;

    /// <summary>
    /// The memory picker's choices: "Folder" plus one per contributed source, in registration order. Left empty
    /// when <c>CreateAsync</c> was given none — which is what makes the picker itself disappear
    /// (<see cref="HasMemorySources"/>) rather than show a dropdown with nothing useful in it.
    /// </summary>
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; } = [];

    /// <summary>Whether the memory picker is shown at all. False keeps the Memory row exactly as it was before AC-166.</summary>
    public bool HasMemorySources => MemorySourceChoices.Count > 0;

    /// <summary>
    /// The picker's current choice, or null when nothing was ever picked — which reads the same as "Folder"
    /// (<see cref="IsMemoryFolderMode"/>) so a dialog with no registered sources needs no selection to behave
    /// correctly.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryFolderMode))]
    [NotifyPropertyChangedFor(nameof(MemoryValuePlaceholder))]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    private MemorySourceChoice? _selectedMemorySourceChoice;

    /// <summary>Whether <see cref="MemoryRef"/> holds a folder path rather than a source's bare value — gates "Choose…", which only ever browses for a folder.</summary>
    public bool IsMemoryFolderMode => SelectedMemorySourceChoice?.Scheme is null;

    /// <summary>
    /// The line under the Memory label, which has to stop calling the location a folder once it is not one. Found by
    /// rendering the row rather than by a test: with a source picked, the hint still read "a folder, kept apart from
    /// the source folder" directly above a box holding a project key — the one sentence on the row insisting on
    /// exactly what this feature exists to stop assuming. The folder wording is unchanged to the character, so a
    /// cockpit with no source registered reads precisely as it did before.
    /// </summary>
    public string MemoryHint =>
        SelectedMemorySourceChoice is { Scheme.Length: > 0 }
            ? "Where this project's memory lives — the name it goes by in the source above, not a path. Sessions are told about it, so they can look things up instead of being told again."
            : "Where this project's memory lives — a folder, kept apart from the source folder. Sessions are told about it, so they can look things up instead of being told again.";

    /// <summary>What the empty memory box hints at: a folder when none is picked, an identifier once a source is.</summary>
    public string MemoryValuePlaceholder =>
        SelectedMemorySourceChoice is { Scheme.Length: > 0 } choice ? $"An identifier {choice.Label} understands" : "No memory location";

    /// <summary>The configured profiles, by label — a project points at one, it does not own one.</summary>
    public ObservableCollection<string> Profiles { get; } = [];

    /// <summary>Every offered MCP server with whether this project's sessions get it. Unticking one is what fills the overlay's disabled list.</summary>
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    /// <summary>
    /// The project's extra information, in the order the operator put it in (AC-295). Rows they add and leave empty
    /// cost them nothing: <see cref="ToProject"/> drops them.
    /// </summary>
    public ObservableCollection<ProjectInfoFieldViewModel> AdditionalInfo { get; } = [];

    /// <summary>
    /// The fields plugins contributed (AC-317), in registration order — what this project is called in a tracker or
    /// on a forge. Empty when no plugin that links projects is installed, and the section stays out of the dialog.
    /// </summary>
    public ObservableCollection<ProjectPluginFieldViewModel> PluginFields { get; } = [];

    public bool HasPluginFields => PluginFields.Count > 0;

    public bool HasMcpServers => McpServers.Count > 0;

    /// <summary>
    /// Fetches every contributed field's choices, all at once and after the dialog is already on screen — both
    /// sources are a network call or a shelled-out CLI, and neither is worth making the operator wait on before
    /// they can start typing a name.
    /// </summary>
    public Task LoadPluginFieldOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(PluginFields.Select(field => field.LoadOptionsAsync(cancellationToken)));

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
            // Resources first, MemoryRef second — deliberately, not stylistically (see Project.MemoryRef's own doc
            // comment): the two write the same underlying list, and whichever is set last in this initializer wins.
            // Carrying the project's other rows through and only then folding the edited memory value in is what
            // keeps an Instructions or Reference row from being replaced by an empty list here.
            Resources = _carriedResources,
            MemoryRef = _ToMemoryRef(),
            McpOverlay = new ProjectMcpOverlay
            {
                DisabledServerNames =
                [
                    .. McpServers.Where(server => !server.IsEnabledForSession).Select(server => server.Name),
                    .. _carriedDisabledServerNames,
                ],
                AdditionalServers = _additionalServers,
            },
            // Tidied here rather than only in the store, so what the caller gets back is what will be saved — an
            // empty row the operator added and left alone is not information, and a pasted value brings newlines
            // the single-line row cannot show.
            AdditionalInfo =
            [
                .. AdditionalInfo.Select(field => field.ToDomain().Tidied()).Where(field => !field.IsBlank),
            ],
            PluginFields = _LinkedProjectFields(),
        };

    /// <summary>
    /// The saved <c>MemoryRef</c>: the folder path as typed in "Folder" mode, unchanged from before this feature
    /// existed, or <c>"{scheme}:{value}"</c> once a source is selected — except a value the operator left blank,
    /// which saves as no reference at all rather than a bare <c>"{scheme}:"</c> that names a source and nothing in it.
    /// </summary>
    private string? _ToMemoryRef() =>
        SelectedMemorySourceChoice is { Scheme: { Length: > 0 } scheme }
            ? _NullIfBlank(MemoryRef) is { } value ? $"{scheme}:{value}" : null
            : _NullIfBlank(MemoryRef);

    /// <summary>
    /// What this project is linked to: the rows the operator filled in, plus the keys carried through from plugins
    /// that are not installed. A row left empty is not written — clearing the box is how a link is removed, and an
    /// empty string under a key would read as "linked to nothing in particular".
    /// </summary>
    private IReadOnlyDictionary<string, string> _LinkedProjectFields()
    {
        var links = new Dictionary<string, string>(_carriedPluginFields, StringComparer.Ordinal);
        foreach (var field in PluginFields.Where(field => !string.IsNullOrWhiteSpace(field.Value)))
        {
            links[field.Key] = field.Value.Trim();
        }

        return links;
    }

    [RelayCommand]
    private void Browse() => BrowseRequested?.Invoke();

    [RelayCommand]
    private void PickLogo() => PickLogoRequested?.Invoke();

    [RelayCommand]
    private void PickMemory() => PickMemoryRequested?.Invoke();

    /// <summary>Drops the logo. The stored copy goes when the project is saved, not here — cancelling must leave it as it was.</summary>
    [RelayCommand]
    private void ClearLogo() => LogoSource = string.Empty;

    [RelayCommand]
    private void AddInfoField() => AdditionalInfo.Add(new ProjectInfoFieldViewModel());

    [RelayCommand]
    private void RemoveInfoField(ProjectInfoFieldViewModel field) => AdditionalInfo.Remove(field);

    [RelayCommand]
    private void Clone() => CloneRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(ToProject());

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    private static string? _NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
