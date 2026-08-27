using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Creating or editing one project (AC-160): its name, folder, the profile it starts under, how that profile
// should behave here, whether its sessions isolate, and which MCP servers they see. Edits a copy and hands the
// result back on Save — the caller owns the list and the persisting, the way the profile editor works.
public partial class ProjectDialogViewModel : ViewModelBase
{
    private readonly string? _projectId;

    // The project exactly as it was loaded, kept only so `ToProject` can carry forward a claimed field's value
    // untouched (AC-604 acceptance criterion 3) — an edit to a field this project's ownership claims must never reach
    // `cockpit.json`, whether or not the control is locked.
    private readonly Project? _originalProject;

    // What LogoSource held when this dialog opened (AC-763) — _BuildLogoEditAsync's own baseline to tell
    // "picked a new logo" from "left it alone", the same role _writeBack.Baseline plays for Name/Description
    // but outside SharedProjectBinding: a downloaded logo never becomes editable text to diff against.
    private readonly string _originalLogoSource = string.Empty;

    // Where (AC-247) SaveAsync writes a claimed field's edit back to, or null for a new project, a project no
    // source claims, or one whose fresh checksum read failed at open time — see CreateAsync's own remarks.
    private ProjectSharedWriteBackContext? _writeBack;

    // What SaveAsync asks whether an extra repository row actually is a git repository. Null for a caller that
    // does not wire one (existing test call sites, design-time/previewer) — the same "no manager, no gate" the
    // New-session dialog already tolerates.
    private readonly IWorktreeManager? _worktreeManager;

    // Raised when the dialog is done: the saved project, or null when the operator cancelled.
    public event Action<Project?>? CloseRequested;

    // Raised when the operator picks "Choose…"; the view opens the folder picker and assigns `SourceDirectory`.
    public event Action? BrowseRequested;

    // Raised when the operator picks "Choose…" on an extra repository row (AC-938); the view opens a folder
    // picker and assigns the result back onto that row's own `ProjectRepositoryRowViewModel.Path`.
    public event Action<ProjectRepositoryRowViewModel>? BrowseRepositoryRequested;

    // Raised when the operator picks "Clone…" (AC-90); the host clones and assigns `SourceDirectory`.
    public event Action? CloneRequested;

    // Raised when the operator wants to pick the logo from a file; the view opens the picker and assigns `LogoSource`.
    public event Action? PickLogoRequested;

    // Raised when the operator picks "Choose…" on a resource row (AC-485); the view opens a folder picker for a Memory
    // row or a file picker for any other role, and assigns the result back onto that row's own
    // `ProjectResourceRowViewModel.Reference` — carrying the row rather than a single dialog-wide value, since AC-485
    public event Action<ProjectResourceRowViewModel>? PickResourceRequested;

    // `edit` is the operator's own typed values, fixed for the whole resolve — never the merged retry SaveAsync may go
    // on to build; `latest` is the fresh remote state the failed write's own re-read already fetched, so the window
    // never has to ask Depot again just to show what changed (AC-247).
    public event Func<SharedProjectDefinitionEdit, SharedProjectBinding, Task<ProjectDefinitionConflictResolution?>>? ConflictRequested;

    // Design-time constructor for the Avalonia previewer.
    public ProjectDialogViewModel()
    {
        Profiles.Add("personal");
        SelectedProfileLabel = "personal";
        Name = "Cockpit";
        AdditionalInfo.Add(new ProjectInfoFieldViewModel("Repository", "https://github.com/example/ai-cockpit", isSharedWithSessions: true));
        AdditionalInfo.Add(new ProjectInfoFieldViewModel("Customer", "Acme BV — ask for their project lead"));
    }

    private ProjectDialogViewModel(Project? project, IWorktreeManager? worktreeManager)
    {
        _projectId = project?.Id;
        _originalProject = project;
        _worktreeManager = worktreeManager;
        IsEditing = project is not null;

        if (project is null)
        {
            return;
        }

        Name = project.Name;
        Category = project.Category ?? string.Empty;
        Description = project.Description ?? string.Empty;
        SourceDirectory = project.SourceDirectory ?? string.Empty;
        GitUrl = project.GitUrl;

        // Item 0 is the Folder row above (SourceDirectory); everything after it is an extra repository row.
        foreach (var repository in project.SourceDirectories.Skip(1))
        {
            RepositoryRows.Add(new ProjectRepositoryRowViewModel(repository.Path, repository.Label));
        }
        BehaviorPrompt = project.BehaviorPrompt ?? string.Empty;
        Assistant = project.Assistant ?? string.Empty;
        LogoSource = project.LogoPath ?? string.Empty;
        _originalLogoSource = LogoSource;
        IsolateInWorktreeByDefault = project.IsolateInWorktreeByDefault;
        _additionalServers = project.McpOverlay.AdditionalServers;
        // Matching a Memory row's reference against a registered source has to wait until CreateAsync has built
        // MemorySourceChoices below — exactly the same ordering problem the single Memory row used to have (see the
        // matching there), just once per row instead of once for the whole dialog.
        _pendingResources = project.Resources;

        foreach (var field in project.AdditionalInfo)
        {
            AdditionalInfo.Add(new ProjectInfoFieldViewModel(field.Label, field.Value, field.IsSharedWithSessions, field.IsSecret));
        }
    }

    // A view model for `project`, or for a new project when it is null, with the profile picker
    // and MCP checklist filled from the stores. An async factory rather than loading in the constructor, because
    // both lists come off disk and a half-populated dialog is worse than one that opens a moment later.
    public static async Task<ProjectDialogViewModel> CreateAsync(
        Project? project,
        ISessionProfileStore profileStore,
        IMcpServerCatalog mcpServerCatalog,
        IReadOnlyList<ProjectFieldRegistration>? pluginFields = null,
        IReadOnlyList<ProjectMemorySourceRegistration>? memorySources = null,
        IReadOnlyList<ProjectMemorySourceFamily>? memorySourceFamilies = null,
        Func<(IReadOnlyList<ProjectMemorySourceRegistration> Sources, IReadOnlyList<ProjectMemorySourceFamily> Families)>? refreshMemorySources = null,
        IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? fieldOwnership = null,
        IReadOnlyList<string>? knownCategories = null,
        // AC-247: null for a new project, a project no source claims, or a claimed project whose fresh checksum read
        // failed — Save then behaves exactly as it did before AC-247 (a locked claimed field, if any, simply never
        // reaches ToProject; see _Carry).
        ProjectSharedWriteBackContext? sharedWriteBack = null,
        // AC-938: the same repository probe the New-session dialog already uses to grey its isolate checkbox — null
        // for a caller that does not wire one, in which case SaveAsync's own repository-is-a-git-repo check is
        // simply skipped, never a hard requirement this dialog did not have before.
        IWorktreeManager? worktreeManager = null,
        CancellationToken cancellationToken = default)
    {
        var viewModel = new ProjectDialogViewModel(project, worktreeManager)
        {
            HasFieldOwnership = fieldOwnership is not null,
            _writeBack = sharedWriteBack,
            NameOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.Name),
            DescriptionOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.Description),
            LogoOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.Logo),
            BehaviorOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.Behavior),
            McpOverlayOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.McpOverlay),
            WorktreeSwitchOrigin = _ResolveOrigin(fieldOwnership, HostProjectField.WorktreeSwitch),
            // AC-523: the same source CreateAsync itself reads from below, kept so a later "Servers…" call
            // (ConfigureMemorySourceAsync) can re-read it once its own settings screen has closed, rather than
            // rebuilding forever from the one-time snapshot memorySources/memorySourceFamilies handed to this call.
            _refreshMemorySources = refreshMemorySources ?? (() => (memorySources ?? [], memorySourceFamilies ?? [])),
        };

        // AC-618: chips for the categories already in use elsewhere (ProjectSettings.CategoryOrder), so picking one
        // is a click instead of retyping a name that has to match case-insensitively to land in the same group.
        foreach (var category in knownCategories ?? [])
        {
            viewModel.CategoryChips.Add(new ProjectCategoryChipViewModel(
                category, string.Equals(category, viewModel.Category, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var registration in pluginFields ?? [])
        {
            viewModel.PluginFields.Add(new ProjectPluginFieldViewModel(
                registration,
                project is null ? null : ProjectLinkValues.Join(project.LinkedAsAll(registration.Key))));
        }

        // AC-499: "Folder" is offered unconditionally, not only once a plugin registered something — the doorless
        // dead end this ticket exists to close. ShowsMemorySourcePicker is true for every Memory row from here on,
        // whatever memorySources/memorySourceFamilies were passed.
        viewModel.MemorySourceChoices.Add(new MemorySourceChoice("Folder", Scheme: null));

        var familyInstances = new Dictionary<string, List<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase);

        // Declared families come next, in declaration order, each its own row in the top picker regardless of how
        // many (if any) instances it has — the empty state has to be reachable from the picker, not only once a
        // first instance exists.
        foreach (var family in memorySourceFamilies ?? [])
        {
            viewModel.MemorySourceChoices.Add(new MemorySourceChoice(family.Title, Scheme: null)
            {
                FamilyKey = family.Key,
                EmptyHint = family.EmptyHint,
                ConfigureAsync = family.ConfigureAsync,
            });
            familyInstances[family.Key] = [];
        }

        // Every registration becomes either its own row (no FamilyKey — exactly the pre-AC-499 behaviour) or one
        // instance under its family's own dropdown, never both.
        foreach (var registration in memorySources ?? [])
        {
            // AC-499 review: InstanceTitle's own doc comment promises "blank or null falls back to Title" — a
            // whitespace-only value (a stray " " a plugin's own settings UI let through untrimmed) is blank in every
            // other sense this codebase uses the word (Register's own IsNullOrWhiteSpace checks), so a plain Length>0
            var instanceChoice = _BuildInstanceChoice(registration);

            if (registration.FamilyKey is { Length: > 0 } familyKey && familyInstances.TryGetValue(familyKey, out var instances))
            {
                instances.Add(instanceChoice);
            }
            else
            {
                viewModel.MemorySourceChoices.Add(instanceChoice);
            }
        }

        viewModel.MemorySourceFamilyInstances = familyInstances.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<MemorySourceChoice>)pair.Value, StringComparer.OrdinalIgnoreCase);

        // Every saved resource becomes a row, in order — the whole of AC-485: what the old single Memory row (and
        // the "carried through untouched" rows behind it) used to hide is now what the operator actually edits.
        foreach (var resource in viewModel._pendingResources)
        {
            var row = new ProjectResourceRowViewModel(
                viewModel.MemorySourceChoices, resource.Role, resource.Reference, resource.Label ?? "", resource.ReachesSessions, resource.SendsContent,
                viewModel.MemorySourceFamilyInstances);

            // The match below overwrites this only when the row is a Memory row whose stored reference actually names a
            // registered source; every other case leaves Folder selected, which is what the ComboBox must show rather
            // than nothing at all.
            row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[0];

            // That is deliberate, not merely the fallback case: a plugin that is temporarily uninstalled must not lose
            // or garble the reference just because this dialog was opened and saved while it was gone (AC-499).
            if (resource.Role == ProjectResourceRole.Memory
                && ProjectMemoryRef.TryParse(resource.Reference, out var scheme, out var value)
                && row.TryMatchMemorySourceScheme(scheme, out var top, out var instance))
            {
                row.SelectedMemorySourceChoice = top;
                row.SelectedFamilyInstance = instance;
                row.Reference = value;
            }

            viewModel._AddResourceRow(row);
        }

        // Awaited directly here (AC-485 review, MUST-FIX 2), not merely scheduled: the dialog must open with every
        // row's diagnostics already answered, the same as before that review moved the actual check off the UI thread.
        viewModel._RefreshResourceDiagnostics(immediately: true);
        await viewModel.ResourceDiagnosticsRefreshCompleted.ConfigureAwait(false);

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

        // AC-766: scoped to this project rather than the project-agnostic GetServersAsync() — the checklist is where a
        // project-linked server (Depot, say) finally gets a row of its own, ticked by default, instead of being
        // invisible here no matter how the operator configured it.
        var servers = await mcpServerCatalog.GetServersForProjectAsync(project?.Id, cancellationToken).ConfigureAwait(false);
        var overlay = project?.McpOverlay ?? ProjectMcpOverlay.None;
        var offered = McpServerRegistryFilter.OfferedToOperator(servers);

        foreach (var server in offered)
        {
            viewModel.McpServers.Add(new McpServerSelectionItemViewModel(server.Name, server.ProjectLinked)
            {
                // The config overload, not the name-only one: a project-linked row starts ticked unless its own
                // DisabledServerNames entry says otherwise, never by whether an operator who never had this row to
                // begin with happened to name it in EnabledServerNames.
                IsEnabledForSession = overlay.IsSelectedByDefault(server),
            });
        }

        // A name this project has already decided about that the checklist cannot show — the server was disabled in the
        // registry since, or removed — is kept rather than dropped on save, the way the project's own servers are.
        var hasRow = offered.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decided = (overlay.EnabledServerNames ?? [])
            .Concat(overlay.DisabledServerNames)
            .Where(name => !hasRow.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        viewModel._carriedEnabledServerNames = [.. decided.Where(overlay.IsSelectedByDefault)];
        viewModel._carriedDisabledServerNames = [.. decided.Where(name => !overlay.IsSelectedByDefault(name))];

        // Opening the editor without reconciling the two would let SaveAsync send the stale local values back to Depot
        // with a checksum that legitimately matches its current state, silently overwriting whatever a colleague
        // changed before this editor ever opened — not a race, a guaranteed clobber on every edit to a project
        if (sharedWriteBack is not null)
        {
            viewModel._ApplyRemoteValues(sharedWriteBack.Baseline);
        }

        return viewModel;
    }

    // The project's own servers, carried through untouched: v1 edits which servers are on, not the servers themselves (see `ToProject`).
    private readonly IReadOnlyList<McpServerConfig> _additionalServers = [];

    // The project's resources exactly as the store loaded them, held only until `ResourceRows` can be built from them
    // (AC-485) — matching a Memory row's reference against a registered source has to wait for `CreateAsync` to
    // populate `MemorySourceChoices` first, the same ordering constraint the single Memory row used to have.
    private readonly IReadOnlyList<ProjectResource> _pendingResources = [];

    // The names this project switched on that the checklist has no row for, carried through so saving cannot switch them off.
    private IReadOnlyList<string> _carriedEnabledServerNames = [];

    // The names this project switched off that the checklist has no row for — kept only so saving still counts the project as one that narrowed its servers.
    private IReadOnlyList<string> _carriedDisabledServerNames = [];

    // The links this project holds under keys no installed plugin registered, carried through so saving cannot drop them.
    private IReadOnlyDictionary<string, string> _carriedPluginFields = ReadOnlyDictionary<string, string>.Empty;

    // Whether this is an existing project rather than a new one — drives the title and the confirm button.
    public bool IsEditing { get; }

    public string DialogTitle => IsEditing ? "Edit project" : "New project";

    public string ConfirmLabel => IsEditing ? "Save" : "Create project";

    // Whether anything ever claimed this project's ownership (AC-604) — gates every origin badge below. False
    // (the default, and the only possibility for a new project) means the dialog draws exactly as it always has:
    // no badge, no locked field, matching AC-166's own "an unclaimed project is unchanged" bar.
    public bool HasFieldOwnership { get; private init; }

    public ProjectFieldOriginViewModel NameOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    public ProjectFieldOriginViewModel DescriptionOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    public ProjectFieldOriginViewModel LogoOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    public ProjectFieldOriginViewModel BehaviorOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    public ProjectFieldOriginViewModel McpOverlayOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    public ProjectFieldOriginViewModel WorktreeSwitchOrigin { get; private init; } = ProjectFieldOriginViewModel.Local;

    private static ProjectFieldOriginViewModel _ResolveOrigin(
        IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>? fieldOwnership, HostProjectField field) =>
        fieldOwnership is not null && fieldOwnership.TryGetValue(field, out var ownership) && ownership is not null
            ? ProjectFieldOriginViewModel.Claimed(ownership.SourceName, ownership.IsEditable, ownership.Role)
            : ProjectFieldOriginViewModel.Local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    // AC-1071: which assistant/persona this project's sessions run as, overriding the profile's. Always local,
    // for the same reason `Category` below is: nothing a shared definition claims can own it, so there is no
    // origin badge and no `_Carry{T}` — a colleague binding this project keeps their own assistant.
    [ObservableProperty]
    private string _assistant = string.Empty;

    // Which category this project sits under in the manager's list (AC-618). Always local — never one of the six
    // claimable `HostProjectField`s, so unlike Name/Description above there is no origin badge and no
    // `_Carry{T}` here: nothing a shared project's definition claims can ever own this field.
    [ObservableProperty]
    private string _category = string.Empty;

    // Empty for the design-time constructor and for a build with no categories in use yet, in which case
    // `HasCategoryChips` is false and the row stays off screen rather than showing an empty bar (AC-618).
    public ObservableCollection<ProjectCategoryChipViewModel> CategoryChips { get; } = [];

    public bool HasCategoryChips => CategoryChips.Count > 0;

    [ObservableProperty]
    private string _description = string.Empty;

    // The project's logo as the operator gave it: a file path, an `http(s)` URL, or the stored copy's path for
    // one already set. Blank means none — and, on save, means removing the one it had.
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

    // Where `SourceDirectory` was cloned from, kept so an edit does not lose it. Set by the clone flow, never typed.
    public string? GitUrl { get; private set; }

    // Always at least "Folder" — the picker no longer disappears (`ProjectResourceRowViewModel.ShowsMemorySourcePicker`
    // is true for every Memory row) just because no plugin registered a source (AC-165, AC-499).
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; } = [];

    // Built in `CreateAsync` and shared by every row in the dialog, the same way `MemorySourceChoices` is — but, unlike
    // that collection, rebuilt (AC-523, not merely "once") by `ConfigureMemorySourceAsync` once its own "Servers…" call
    // returns, and pushed back onto every row via (AC-499).
    public IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>> MemorySourceFamilyInstances { get; private set; } =
        new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase);

    // Re-reads the live memory-source registry (AC-523) — the same source `CreateAsync`'s own
    // `memorySources`/`memorySourceFamilies` parameters were read from, reused rather than a new dependency injected
    // here — so `ConfigureMemorySourceAsync` can rebuild `MemorySourceFamilyInstances` after its "Servers…" call
    private Func<(IReadOnlyList<ProjectMemorySourceRegistration> Sources, IReadOnlyList<ProjectMemorySourceFamily> Families)> _refreshMemorySources =
        () => ([], []);

    // The configured profiles, by label — a project points at one, it does not own one.
    public ObservableCollection<string> Profiles { get; } = [];

    // Every offered MCP server with whether this project's sessions get it. Unticking one is what fills the overlay's disabled list.
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    // The project's extra information, in the order the operator put it in (AC-295). Rows they add and leave empty
    // cost them nothing: `ToProject` drops them.
    public ObservableCollection<ProjectInfoFieldViewModel> AdditionalInfo { get; } = [];

    // The project's resources (AC-483/485), in the order the operator put them in — a memory location, standing
    // instructions, something to look up.
    public ObservableCollection<ProjectResourceRowViewModel> ResourceRows { get; } = [];

    // Repository #2 and on (AC-938) — the Folder row above stays repo #1 (SourceDirectory). A row the operator
    // adds and leaves blank is refused at save (see SaveAsync), not silently dropped: a gap in this list is not
    // this project's repositories, it is a mistake.
    public ObservableCollection<ProjectRepositoryRowViewModel> RepositoryRows { get; } = [];

    // The fields plugins contributed (AC-317), in registration order — what this project is called in a tracker or
    // on a forge. Empty when no plugin that links projects is installed, and the section stays out of the dialog.
    public ObservableCollection<ProjectPluginFieldViewModel> PluginFields { get; } = [];

    public bool HasPluginFields => PluginFields.Count > 0;

    public bool HasMcpServers => McpServers.Count > 0;

    // Fetches every contributed field's choices, all at once and after the dialog is already on screen — both
    // sources are a network call or a shelled-out CLI, and neither is worth making the operator wait on before
    // they can start typing a name.
    public Task LoadPluginFieldOptionsAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(PluginFields.Select(field => field.LoadOptionsAsync(cancellationToken)));

    // A project needs a name — it is what every other surface shows it by.
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    // AC-247: set while SaveAsync's own write-back call (and any conflict re-read/retry) is in flight — the view
    // disables Save and shows a spinner rather than letting a second click start a second, overlapping write.
    [ObservableProperty]
    private bool _isSaving;

    // AC-247: the reason a write-back failed outright (PermissionDenied or an unclassified Failed) — shown as a
    // banner instead of closing the dialog. Null the rest of the time, including for a checksum conflict, which
    // opens its own window (ConflictRequested) rather than showing text here.
    [ObservableProperty]
    private string? _saveError;

    // Assigns a folder chosen by the picker, dropping a stale clone URL when the operator points the project somewhere else.
    public void ApplyPickedDirectory(string directory, string? gitUrl = null)
    {
        SourceDirectory = directory;
        GitUrl = gitUrl;
    }

    // The edited values as a project, reusing the id when editing so the sessions and settings that reference it keep pointing at the same one.
    public Project ToProject()
    {
        var editedOverlay = new ProjectMcpOverlay
        {
            // A list only for a project that actually narrowed something.
            EnabledServerNames = _ComputeEnabledMcpServerNames(),
            // AC-766: a project-linked row unticked here — never one this project had a row for before its own
            // catalog query named the row's scheme — is the one decision EnabledServerNames cannot express; see
            // ProjectMcpOverlay.IsSelectedByDefault(McpServerConfig).
            DisabledServerNames =
            [
                .. McpServers.Where(server => server.IsProjectLinked && !server.IsEnabledForSession).Select(server => server.Name),
                .. _carriedDisabledServerNames,
            ],
            AdditionalServers = _additionalServers,
        };

        return new(_projectId ?? Guid.NewGuid().ToString("n"), _Carry(NameOrigin, Name.Trim(), p => p.Name))
        {
            Category = _NullIfBlank(Category),
            Assistant = _NullIfBlank(Assistant),
            Description = _Carry(DescriptionOrigin, _NullIfBlank(Description), p => p.Description),
            // Item 0 is this dialog's own SourceDirectory box, exactly as it always was — a project with no folder
            // and no repository rows saves an empty list, same as an empty SourceDirectory used to save null.
            SourceDirectories = _NullIfBlank(SourceDirectory) is { } folder
                ? [new(folder), .. RepositoryRows.Select(row => row.ToDomain())]
                : [],
            GitUrl = GitUrl,
            DefaultProfileLabel = SelectedProfileLabel,
            BehaviorPrompt = _Carry(BehaviorOrigin, _NullIfBlank(BehaviorPrompt), p => p.BehaviorPrompt),
            // What the operator pointed at — a file, a URL, or the stored copy's path when they left it alone. The
            // manager turns it into a copy the cockpit owns; the editor only carries the answer, as it does the rest.
            LogoPath = _Carry(LogoOrigin, _NullIfBlank(LogoSource), p => p.LogoPath),
            IsolateInWorktreeByDefault = _Carry(WorktreeSwitchOrigin, IsolateInWorktreeByDefault, p => p.IsolateInWorktreeByDefault),
            // Resources only — never MemoryRef beside it (see Project.MemoryRef's own doc comment on why an initializer
            // must pick one: both write the same underlying list, and whichever is set last wins).
            Resources =
            [
                .. ResourceRows.Select(row => row.ToDomain()).Where(resource => !string.IsNullOrWhiteSpace(resource.Reference)),
            ],
            McpOverlay = _Carry(McpOverlayOrigin, editedOverlay, p => p.McpOverlay),
            // Tidied here rather than only in the store, so what the caller gets back is what will be saved — an
            // empty row the operator added and left alone is not information, and a pasted value brings newlines
            // the single-line row cannot show.
            AdditionalInfo =
            [
                .. AdditionalInfo.Select(field => field.ToDomain().Tidied()).Where(field => !field.IsBlank),
            ],
            PluginFields = _LinkedProjectFields(),
            // AC-607: no dialog field edits this yet (out of scope for this ticket) — carried through unedited
            // rather than silently dropped on save, the same as any other field with no editing surface.
            ProjectPassword = _originalProject?.ProjectPassword,
        };
    }

    // `edited` unless `origin` says this field is both claimed and still locked (AC-604 acceptance criterion 3,
    // narrowed by AC-247), in which case the value `_originalProject` already had wins instead — an edit to a field
    // with nowhere to write back to must never reach `cockpit.json`, whether or not the control let the operator type
    private T _Carry<T>(ProjectFieldOriginViewModel origin, T edited, Func<Project, T> original) =>
        origin.IsClaimed && origin.IsLockedHere && _originalProject is not null ? original(_originalProject) : edited;

    // What this project is linked to: the rows the operator filled in, plus the keys carried through from plugins
    // that are not installed. A row left empty is not written — clearing the box is how a link is removed, and an
    // empty string under a key would read as "linked to nothing in particular".
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

    // Drops the logo. The stored copy goes when the project is saved, not here — cancelling must leave it as it was.
    [RelayCommand]
    private void ClearLogo() => LogoSource = string.Empty;

    // A category chip's click (AC-618): fills the field exactly as if the operator had typed it — no second, chip-only path onto the saved project.
    [RelayCommand]
    private void SelectCategory(string category) => Category = category;

    [RelayCommand]
    private void AddInfoField() => AdditionalInfo.Add(new ProjectInfoFieldViewModel());

    [RelayCommand]
    private void RemoveInfoField(ProjectInfoFieldViewModel field) => AdditionalInfo.Remove(field);

    // AC-499 review, defect found by `Cockpit.App.ViewTests.ProjectDialogMemorySourceTests` (which builds a
    // `ProjectDialogViewModel` directly rather than through `CreateAsync`, exactly the same shape the XAML designer's
    // own `&lt;Design.DataContext&gt;` instance uses): `MemorySourceChoices[0]` is only guaranteed to exist once
    [RelayCommand]
    private void AddResourceRow()
    {
        var row = new ProjectResourceRowViewModel(MemorySourceChoices, familyInstanceChoicesByKey: MemorySourceFamilyInstances)
        {
            SelectedMemorySourceChoice = MemorySourceChoices.Count > 0 ? MemorySourceChoices[0] : null,
        };

        _AddResourceRow(row);
        _RefreshResourceDiagnostics();
    }

    [RelayCommand]
    private void RemoveResourceRow(ProjectResourceRowViewModel row)
    {
        ResourceRows.Remove(row);
        _UpdateLastRowFlags();
        _RefreshResourceDiagnostics();
    }

    [RelayCommand]
    private void PickResource(ProjectResourceRowViewModel row) => PickResourceRequested?.Invoke(row);

    // Appends a blank extra repository row (AC-938), the same shape AddResourceRow already has.
    [RelayCommand]
    private void AddRepositoryRow() => RepositoryRows.Add(new ProjectRepositoryRowViewModel());

    [RelayCommand]
    private void RemoveRepositoryRow(ProjectRepositoryRowViewModel row) => RepositoryRows.Remove(row);

    [RelayCommand]
    private void BrowseRepository(ProjectRepositoryRowViewModel row) => BrowseRepositoryRequested?.Invoke(row);

    // Also a no-op while a previous call for the same row is still running
    // (`ProjectResourceRowViewModel.IsConfiguringMemorySource`) — an impatient second click must not start a second,
    // overlapping call to the same plugin (AC-499, AC-523).
    [RelayCommand]
    private async Task ConfigureMemorySourceAsync(ProjectResourceRowViewModel row)
    {
        if (row.SelectedMemorySourceChoice?.ConfigureAsync is not { } configureAsync || row.IsConfiguringMemorySource)
        {
            return;
        }

        row.IsConfiguringMemorySource = true;
        row.MemorySourceConfigureError = null;
        try
        {
            await configureAsync(CancellationToken.None).ConfigureAwait(true);

            var (sources, families) = _refreshMemorySources();
            MemorySourceFamilyInstances = _BuildFamilyInstances(sources, families);

            foreach (var resourceRow in ResourceRows)
            {
                resourceRow.UpdateFamilyInstanceChoices(MemorySourceFamilyInstances);
            }
        }
        catch (Exception exception)
        {
            row.MemorySourceConfigureError = exception.Message;
        }
        finally
        {
            row.IsConfiguringMemorySource = false;
        }
    }

    [RelayCommand]
    private void Clone() => CloneRequested?.Invoke();

    // Only a project with a live `_writeBack` context takes the write-then-close path below, and only that path can
    // come back with SaveError set or reopen this same dialog after a resolved conflict instead of closing it (AC-247).
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        SaveError = null;

        if (await _ValidateRepositoryRowsAsync().ConfigureAwait(true) is { } repositoryError)
        {
            SaveError = repositoryError;
            return;
        }

        if (_writeBack is not { } writeBack)
        {
            CloseRequested?.Invoke(ToProject());
            return;
        }

        // Defensive: a second click while the first write (or its conflict window) is still in flight must not
        // start a second, overlapping write.
        if (IsSaving)
        {
            return;
        }

        SaveError = null;

        // Compare edits with the opening baseline, never a merged retry, so untouched fields remain distinguishable.
        var operatorEdit = await _BuildEditAsync().ConfigureAwait(true);

        // Skip shared writes entirely when no shared field changed; a harmless round trip still creates needless
        // conflict risk.
        if (_MatchesBaseline(operatorEdit, writeBack.Baseline))
        {
            CloseRequested?.Invoke(ToProject());
            return;
        }

        IsSaving = true;
        try
        {
            var pendingEdit = operatorEdit;
            var baseChecksum = writeBack.Baseline.Checksum!;

            while (true)
            {
                var result = await writeBack.Source.WriteBackAsync(writeBack.Id, pendingEdit, baseChecksum, CancellationToken.None)
                    .ConfigureAwait(true);

                if (result.Outcome == SharedProjectWriteBackOutcome.Success)
                {
                    // `pendingEdit` — not `operatorEdit` — is what actually landed at the source: on a merge retry
                    // (below) they differ on every field the operator left alone, and cockpit.json must carry the
                    // same values Depot just accepted, not what the operator's own untouched fields still show.
                    _ApplyEditValues(pendingEdit);
                    CloseRequested?.Invoke(ToProject());
                    return;
                }

                if (result.Outcome != SharedProjectWriteBackOutcome.ChecksumConflict)
                {
                    // PermissionDenied or Failed: never retried automatically (AC-247 — a rejected write is not a
                    // conflict to merge, it is a reason to stop and say why). The operator stays in this dialog;
                    // SaveError is what the view shows instead of closing.
                    SaveError = result.Error ?? "Could not save this project.";
                    return;
                }

                if (ConflictRequested is null)
                {
                    // No one is listening to show the conflict window (a design-time instance, a test harness with
                    // no dialog service wired) — fail closed rather than silently overwrite or silently drop the edit.
                    SaveError = "This project changed elsewhere; reopen it to see the latest version.";
                    return;
                }

                var resolution = await ConflictRequested.Invoke(operatorEdit, result.LatestSnapshot!).ConfigureAwait(true);
                if (resolution is null)
                {
                    // The operator dismissed the conflict window — back to editing here, nothing written yet.
                    return;
                }

                if (resolution.TakeTheirs)
                {
                    _ApplyRemoteValues(result.LatestSnapshot!);
                    CloseRequested?.Invoke(ToProject());
                    return;
                }

                // Merge only touched fields onto fresh remote state so a colleague's unrelated edits survive.
                pendingEdit = _MergeOntoLatest(operatorEdit, writeBack.Baseline, result.LatestSnapshot!);
                baseChecksum = result.LatestSnapshot!.Checksum!;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // AC-938 acceptance criterion 3: refused before ToProject ever builds SourceDirectories, not silently dropped
    // or silently saved with a gap — the same "fail closed, tell the operator why" SaveError already gives a
    // write-back conflict. Returns the refusal reason, or null when every row is fine to save.
    private async Task<string?> _ValidateRepositoryRowsAsync()
    {
        var extraRepositories = RepositoryRows.Select(row => row.Path.Trim()).ToList();

        if (extraRepositories.Any(path => path.Length == 0))
        {
            return "Every repository row needs a folder — remove an empty row instead of leaving it blank.";
        }

        if (extraRepositories.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(SourceDirectory))
        {
            return "Choose the project's own folder before adding another repository.";
        }

        if (_worktreeManager is null)
        {
            return null;
        }

        foreach (var path in extraRepositories)
        {
            if (await _worktreeManager.DetectRepositoryAsync(path).ConfigureAwait(true) is null)
            {
                return $"'{path}' is not a git repository.";
            }
        }

        return null;
    }

    // What the operator typed for the six write-back-eligible fields (AC-247/AC-763).
    private async Task<SharedProjectDefinitionEdit> _BuildEditAsync() => new(
        Name.Trim(),
        _NullIfBlank(Description),
        _NullIfBlank(BehaviorPrompt),
        IsolateInWorktreeByDefault,
        _ComputeEnabledMcpServerNames(),
        await _BuildLogoEditAsync().ConfigureAwait(true));

    // Null (untouched) unless LogoSource moved from what this dialog opened with (AC-763) — LogoSource is always a
    // local file path here (PickLogo's own picker, or the already-stored copy's path; never a URL an operator
    // types in, unlike IProjectLogoStore.SaveAsync's own broader contract), so reading it is a plain file read.
    private async Task<SharedProjectLogoEdit?> _BuildLogoEditAsync()
    {
        var edited = _NullIfBlank(LogoSource);
        if (string.Equals(edited, _NullIfBlank(_originalLogoSource), StringComparison.Ordinal))
        {
            return null;
        }

        if (edited is null)
        {
            return SharedProjectLogoEdit.Cleared;
        }

        try
        {
            return SharedProjectLogoEdit.Replace(await File.ReadAllBytesAsync(edited).ConfigureAwait(true));
        }
        catch (Exception)
        {
            // A logo is decoration (ProjectLogoStore.SaveAsync's own reasoning): a file that vanished between
            // picking and saving costs the picture, not the rest of this save.
            return null;
        }
    }

    // The same "null means no opinion, otherwise every ticked ordinary name plus whatever this build has no row for"
    // logic both ToProject's own overlay and AC-247's remote edit need (AC-766, AC-763).
    private IReadOnlyList<string>? _ComputeEnabledMcpServerNames() =>
        McpServers.Any(server => !server.IsProjectLinked && !server.IsEnabledForSession) || _carriedEnabledServerNames.Count > 0 || _carriedDisabledServerNames.Count > 0
            ? [.. McpServers.Where(server => !server.IsProjectLinked && server.IsEnabledForSession).Select(server => server.Name), .. _carriedEnabledServerNames]
            : null;

    // "Hun versie nemen" (AC-247): adopts the fresh remote state onto every claimed, editable field this dialog
    // shows — the operator's own edit to any of them is discarded, exactly what that button promises. Local-only
    // fields (Profile, Folder) are untouched; they were never part of the write-back to begin with.
    private void _ApplyRemoteValues(SharedProjectBinding latest)
    {
        _ApplyValues(latest.Name, latest.Description, latest.BehaviorPrompt, latest.IsolateInWorktreeByDefault, latest.EnabledMcpServerNames);

        // AC-763: `latest` carries no fresh logo bytes (see _MergeOntoLatest's own remarks) to show instead, so
        // the closest this button can do is discard whatever the operator picked here and fall back to what this
        // dialog opened with — never leave the operator's own unsent pick on screen after "take theirs".
        LogoSource = _originalLogoSource;
    }

    // SaveAsync's own success path (both the plain write and a resolved merge retry): what actually reached the
    // source is what belongs on screen and in ToProject's own output — see SaveAsync's remarks on why `pendingEdit`,
    // not `operatorEdit`, is what this is called with on a merge retry.
    private void _ApplyEditValues(SharedProjectDefinitionEdit edit) => _ApplyValues(
        edit.Name, edit.Description, edit.BehaviorPrompt, edit.IsolateInWorktreeByDefault, edit.EnabledMcpServerNames);

    private void _ApplyValues(string name, string? description, string? behaviorPrompt, bool isolate, IReadOnlyList<string>? enabledMcpServerNames)
    {
        Name = name;
        Description = description ?? string.Empty;
        BehaviorPrompt = behaviorPrompt ?? string.Empty;
        IsolateInWorktreeByDefault = isolate;

        var enabled = enabledMcpServerNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var server in McpServers)
        {
            server.IsEnabledForSession = enabled is null || enabled.Contains(server.Name);
        }
    }

    // Whether SaveAsync's own write-back can be skipped outright — every write-back-eligible field reads the same as
    // `baseline`, the read this editor opened with (see CreateAsync's own remarks on why these fields start out equal
    // to it).
    private static bool _MatchesBaseline(SharedProjectDefinitionEdit edit, SharedProjectBinding baseline) =>
        edit.LogoEdit is null
        && _FieldEquals(edit.Name, baseline.Name)
        && _FieldEquals(edit.Description, baseline.Description)
        && _FieldEquals(edit.BehaviorPrompt, baseline.BehaviorPrompt)
        && edit.IsolateInWorktreeByDefault == baseline.IsolateInWorktreeByDefault
        && _SameNames(edit.EnabledMcpServerNames, baseline.EnabledMcpServerNames);

    // Apply operator changes field by field while preserving newer remote values for untouched fields (AC-247).
    private static SharedProjectDefinitionEdit _MergeOntoLatest(
        SharedProjectDefinitionEdit mine, SharedProjectBinding baseline, SharedProjectBinding latest) => new(
        !_FieldEquals(mine.Name, baseline.Name) ? mine.Name : latest.Name,
        !_FieldEquals(mine.Description, baseline.Description) ? mine.Description : latest.Description,
        !_FieldEquals(mine.BehaviorPrompt, baseline.BehaviorPrompt) ? mine.BehaviorPrompt : latest.BehaviorPrompt,
        mine.IsolateInWorktreeByDefault != baseline.IsolateInWorktreeByDefault ? mine.IsolateInWorktreeByDefault : latest.IsolateInWorktreeByDefault,
        !_SameNames(mine.EnabledMcpServerNames, baseline.EnabledMcpServerNames) ? mine.EnabledMcpServerNames : latest.EnabledMcpServerNames,
        // AC-763: no "latest" logo to fall back to (SharedProjectBinding carries no re-applicable edit) —
        // mine.LogoEdit carries through unconditionally. A retried null (untouched) still resolves correctly:
        // WriteBackAsync re-reads its current state on every call and carries an untouched logo from that.
        mine.LogoEdit);

    private static bool _FieldEquals(string? left, string? right) =>
        string.Equals(_NullIfBlank(left ?? string.Empty), _NullIfBlank(right ?? string.Empty), StringComparison.Ordinal);

    private static bool _SameNames(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is null && right is null
        || left is not null && right is not null && left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    // Keeps each chip's `ProjectCategoryChipViewModel.IsActive` matching the field as the operator types or clicks a chip — case-insensitively, the same comparison `ProjectSettings.CategoryOrder` itself groups by.
    partial void OnCategoryChanged(string value)
    {
        foreach (var chip in CategoryChips)
        {
            chip.IsActive = string.Equals(chip.Name, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // Re-running the repo-relative-fix check whenever the folder itself changes (AC-605 criterion 5) — a row already offering "make repo-relative" may no longer once the operator points the project at a folder that no longer contains it, or the reverse.
    partial void OnSourceDirectoryChanged(string value) => _RefreshResourceDiagnostics();

    private void _AddResourceRow(ProjectResourceRowViewModel row)
    {
        ResourceRows.Add(row);
        _UpdateLastRowFlags();
        // AC-485 review (FIX 6) — Role is included, but not for either reason this comment used to give: Role does not
        // gate the broken-reference probe (ReachesSessions does, see _RefreshResourceDiagnostics below), and it does
        // not gate scope either (ProjectResourcePathPortability.ClassifyScope never looks at a row's Role at all).
        row.PropertyChanged += _OnResourceRowChanged;
    }

    // Sets `ProjectResourceRowViewModel.IsLastRow` on every row (AC-485 review, FIX 8) — called
    // whenever `ResourceRows` gains or loses a row, since which one is last can only change then.
    private void _UpdateLastRowFlags()
    {
        for (var i = 0; i < ResourceRows.Count; i++)
        {
            ResourceRows[i].IsLastRow = i == ResourceRows.Count - 1;
        }
    }

    // One family member as the picker offers it — shared by `CreateAsync`'s own first build and
    // `_BuildFamilyInstances`'s later rebuilds (AC-523), so both ever construct a
    // `MemorySourceChoice` for a `ProjectMemorySourceRegistration` the same way.
    private static MemorySourceChoice _BuildInstanceChoice(ProjectMemorySourceRegistration registration) =>
        new(string.IsNullOrWhiteSpace(registration.InstanceTitle) ? registration.Title : registration.InstanceTitle, registration.Scheme)
        {
            ListLocationsAsync = registration.ListLocationsAsync,
            SignInAsync = registration.SignInAsync,
            CheckReachability = registration.CheckReachability,
        };

    // Ungrouped sources are not represented here — this ticket's scope is a family's own instances (AC-523's "Servers…"
    // flow only ever adds or removes those), not `MemorySourceChoices`'s own top-level rows.
    private static IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>> _BuildFamilyInstances(
        IReadOnlyList<ProjectMemorySourceRegistration>? sources,
        IReadOnlyList<ProjectMemorySourceFamily>? families)
    {
        var familyInstances = new Dictionary<string, List<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in families ?? [])
        {
            familyInstances[family.Key] = [];
        }

        foreach (var registration in sources ?? [])
        {
            if (registration.FamilyKey is { Length: > 0 } familyKey && familyInstances.TryGetValue(familyKey, out var instances))
            {
                instances.Add(_BuildInstanceChoice(registration));
            }
        }

        return familyInstances.ToDictionary(
            pair => pair.Key, pair => (IReadOnlyList<MemorySourceChoice>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void _OnResourceRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectResourceRowViewModel.Reference)
            or nameof(ProjectResourceRowViewModel.Role)
            or nameof(ProjectResourceRowViewModel.ReachesSessions)
            or nameof(ProjectResourceRowViewModel.SelectedMemorySourceChoice))
        {
            _RefreshResourceDiagnostics();
        }
    }

    // Completes once the most recently scheduled `_RefreshResourceDiagnostics` call has written its answer onto every
    // row, or been superseded by a later one (AC-485 review, MUST-FIX 2) — a hook a test can await deterministically
    // instead of sleeping for a computation whose entire point is to run off the UI thread.
    internal Task ResourceDiagnosticsRefreshCompleted { get; private set; } = Task.CompletedTask;

    // Bumped by every call to `_RefreshResourceDiagnostics` — see that method's remarks on why a stale answer must be told apart from the current one.
    private int _resourceDiagnosticsRefreshVersion;

    // Schedules a re-run of `ProjectResourceProbe` and `ProjectResourcePathPortability` over every row without waiting
    // for it, so the row-level property change that triggered this call (a keystroke in the Reference box, a role
    // switch, adding or removing a row, the folder changing) returns to the UI immediately (AC-485).
    private void _RefreshResourceDiagnostics(bool immediately = false) =>
        ResourceDiagnosticsRefreshCompleted = _RunResourceDiagnosticsAsync(
            immediately ? TimeSpan.Zero : ResourceDiagnosticsQuietPeriod);

    // Long enough that writing a path straight through never triggers a check, short enough that the answer feels like
    // it belongs to what was just typed.
    private static readonly TimeSpan ResourceDiagnosticsQuietPeriod = TimeSpan.FromMilliseconds(400);

    // The actual work `_RefreshResourceDiagnostics` schedules (AC-485 review, MUST-FIX 2) (AC-503).
    private CancellationTokenSource? _reachabilityCancellation;

    private async Task _RunResourceDiagnosticsAsync(TimeSpan quietPeriod)
    {
        var version = ++_resourceDiagnosticsRefreshVersion;

        // AC-503: retiring the previous call's in-flight reachability checks (if any) the instant a newer one is
        // scheduled — before the quiet-period wait even starts, so a rapid run of edits (simulated typing) never has
        // two overlapping checks racing to answer the same row.
        _reachabilityCancellation?.Cancel();
        _reachabilityCancellation?.Dispose();
        var reachabilityCancellation = new CancellationTokenSource();
        _reachabilityCancellation = reachabilityCancellation;

        // Running the probe off the UI thread stops it freezing the window, but it does not stop it running once per
        // character — and a half-typed path is a path that does not exist, so without this the row flashes "could not
        // be found" in red while the operator is still writing it.
        if (quietPeriod > TimeSpan.Zero)
        {
            await Task.Delay(quietPeriod).ConfigureAwait(true);
            if (version != _resourceDiagnosticsRefreshVersion)
            {
                return;
            }
        }

        var resources = ResourceRows.Select(row => (row, resource: row.ToDomain())).ToList();
        var sourceDirectory = SourceDirectory;

        var fsProbeTask = Task.Run(() =>
        {
            var unresolvedReferences = ProjectResourceProbe.FindUnresolved(resources.Select(pair => pair.resource));
            var scopes = resources.ToDictionary(
                pair => pair.row,
                pair => ProjectResourcePathPortability.ClassifyScope(pair.resource.Reference));
            var repoRelativeFixes = resources.ToDictionary(
                pair => pair.row,
                pair => ProjectResourcePathPortability.SuggestRepoRelativeFix(sourceDirectory, pair.resource.Reference));
            return (Unresolved: unresolvedReferences, Scopes: scopes, RepoRelativeFixes: repoRelativeFixes);
        });

        // AC-503: a Memory row whose picked source has a reachability check and a non-blank typed value gets one, run
        // alongside the filesystem probe above rather than after it — this is a network call and the two probes judge
        // disjoint sets of rows (this one only ever looks at Memory rows; ProjectResourceProbe never does (AC-499).
        var reachabilityTasks = resources
            .Where(pair => pair.row.Role == ProjectResourceRole.Memory
                && pair.row.SelectedMemorySourceLeaf?.CheckReachability is not null
                && !string.IsNullOrWhiteSpace(pair.row.Reference))
            .Select(pair => _RunReachabilityCheckAsync(
                pair.row,
                pair.row.Reference.Trim(),
                pair.row.SelectedMemorySourceLeaf!.CheckReachability!,
                version,
                reachabilityCancellation.Token))
            .ToList();

        var (unresolved, scopes, repoRelativeFixes) = await fsProbeTask.ConfigureAwait(true);
        await Task.WhenAll(reachabilityTasks).ConfigureAwait(true);

        if (version != _resourceDiagnosticsRefreshVersion)
        {
            return;
        }

        foreach (var (row, resource) in resources)
        {
            row.IsBroken = resource.ReachesSessions && unresolved.Contains(resource.Reference);
            row.Scope = scopes[row];
            row.RepoRelativeFix = repoRelativeFixes[row];
        }
    }

    // Apply memory reachability only while its diagnostics version is current, preventing stale async results
    // (AC-503).
    private async Task _RunReachabilityCheckAsync(
        ProjectResourceRowViewModel row,
        string value,
        Func<string, CancellationToken, Task<ProjectMemorySourceReachabilityResult>> check,
        int version,
        CancellationToken cancellationToken)
    {
        ProjectMemorySourceReachabilityResult result;
        try
        {
            result = await check(value, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer edit — the cancellation itself is this call's "stale, drop it" signal, the same
            // as the filesystem probe's own version check answers for its slower-but-uncancellable path.
            return;
        }
        catch (Exception exception)
        {
            // Never NotFound for a failure this ambiguous (AC-503 acceptance criterion 4), which would name the wrong
            // cause for what might simply be a hiccup in the plugin's own check (AC-499).
            result = ProjectMemorySourceReachabilityResult.CheckFailed(exception.Message);
        }

        if (version != _resourceDiagnosticsRefreshVersion)
        {
            return;
        }

        row.Reachability = result.State;
        row.ReachabilityDetail = result.State is ProjectMemorySourceReachability.Confirmed or ProjectMemorySourceReachability.CheckFailed
            ? result.Detail
            : null;
    }

    private static string? _NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// Where SaveAsync writes a claimed field's edit back to (AC-247) — the source that claimed this project, the id it
// knows this project by, and the read this editor opened with (its own Checksum is what the first write attempt
// defends; its other fields are what SaveAsync's per-field merge compares an edit against to tell "the operator touched
public sealed record ProjectSharedWriteBackContext(ISharedProjectSource Source, string Id, SharedProjectBinding Baseline);

// The operator's choice on ProjectDialogViewModel's conflict window (AC-247) — `TakeTheirs` discards every edit to
// a claimed field in favour of the fresh remote state; the other path (SaveAsync's own per-field merge) needs no
// flag of its own; it is what happens whenever this record is not the answer.
public sealed record ProjectDefinitionConflictResolution(bool TakeTheirs);
