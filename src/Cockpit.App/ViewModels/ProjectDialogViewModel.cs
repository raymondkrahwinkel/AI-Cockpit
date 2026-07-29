using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
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

    /// <summary>
    /// Raised when the operator picks "Choose…" on a resource row (AC-485); the view opens a folder picker for a
    /// Memory row or a file picker for any other role, and assigns the result back onto that row's own
    /// <see cref="ProjectResourceRowViewModel.Reference"/> — carrying the row rather than a single dialog-wide value,
    /// since AC-485 lets more than one row need a picker of its own.
    /// </summary>
    public event Action<ProjectResourceRowViewModel>? PickResourceRequested;

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

        // Nothing registered leaves the picker out entirely (ShowsMemorySourcePicker on every row) — a Memory row
        // keeps looking and behaving exactly as it did before AC-166 existed, which is the default this feature
        // must not shift.
        foreach (var registration in memorySources ?? [])
        {
            if (viewModel.MemorySourceChoices.Count == 0)
            {
                viewModel.MemorySourceChoices.Add(new MemorySourceChoice("Folder", Scheme: null));
            }

            viewModel.MemorySourceChoices.Add(new MemorySourceChoice(registration.Title, registration.Scheme)
            {
                CheckReachability = registration.CheckReachability,
            });
        }

        // Every saved resource becomes a row, in order — the whole of AC-485: what the old single Memory row (and
        // the "carried through untouched" rows behind it) used to hide is now what the operator actually edits.
        foreach (var resource in viewModel._pendingResources)
        {
            var row = new ProjectResourceRowViewModel(
                viewModel.MemorySourceChoices, resource.Role, resource.Reference, resource.Label ?? "", resource.ReachesSessions, resource.SendsContent);

            // Folder is the default selection the instant there is a picker at all — a plain path, a reference
            // naming no installed source, or a role other than Memory. The match below overwrites this only when
            // the row is a Memory row whose stored reference actually names a registered source; every other case
            // leaves Folder selected, which is what the ComboBox must show rather than nothing at all.
            if (viewModel.MemorySourceChoices.Count > 0)
            {
                row.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[0];
            }

            // A saved reference of the shape "<scheme>:<value>" naming a source actually offered here selects that
            // source and shows the bare value; anything else — a path, a scheme no installed plugin registered, an
            // empty value after the colon — leaves "Folder" selected (set above) and the reference exactly as the
            // row stored it. That is deliberate, not merely the fallback case: a plugin that is temporarily
            // uninstalled must not lose or garble the reference just because this dialog was opened and saved
            // while it was gone.
            if (resource.Role == ProjectResourceRole.Memory
                && viewModel.MemorySourceChoices.Count > 0
                && ProjectMemoryRef.TryParse(resource.Reference, out var scheme, out var value)
                && viewModel.MemorySourceChoices.FirstOrDefault(choice =>
                    choice.Scheme is { } candidate && string.Equals(candidate, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched)
            {
                row.SelectedMemorySourceChoice = matched;
                row.Reference = value;
            }

            viewModel._AddResourceRow(row);
        }

        // Awaited directly here (AC-485 review, MUST-FIX 2), not merely scheduled: the dialog must open with every
        // row's diagnostics already answered, the same as before that review moved the actual check off the UI
        // thread. Every other call site below only schedules the refresh and moves on — see
        // _RefreshResourceDiagnostics's own remarks on why that is safe for them but not for this one.
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
    /// The project's resources exactly as the store loaded them, held only until <see cref="ResourceRows"/> can be
    /// built from them (AC-485) — matching a Memory row's reference against a registered source has to wait for
    /// <c>CreateAsync</c> to populate <see cref="MemorySourceChoices"/> first, the same ordering constraint the
    /// single Memory row used to have. Empty once <c>CreateAsync</c> has consumed it; nothing reads it afterwards.
    /// </summary>
    private readonly IReadOnlyList<ProjectResource> _pendingResources = [];

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
    /// The memory-source picker's choices, shared by every <see cref="ProjectResourceRowViewModel"/> whose
    /// <see cref="ProjectResourceRowViewModel.Role"/> is Memory: "Folder" plus one per contributed source, in
    /// registration order (AC-165/166). Left empty when <c>CreateAsync</c> was given none — which is what makes the
    /// picker disappear from every row (<see cref="ProjectResourceRowViewModel.ShowsMemorySourcePicker"/>) rather
    /// than show a dropdown with nothing useful in it.
    /// </summary>
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; } = [];

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
    /// The project's resources (AC-483/485), in the order the operator put them in — a memory location, standing
    /// instructions, something to look up. Replaces the dialog's old standalone Memory row: that row is now simply
    /// one of these with <see cref="ProjectResourceRowViewModel.Role"/> set to <see cref="ProjectResourceRole.Memory"/>,
    /// and every other role that a project could already carry (but this dialog had no box for) is edited here too.
    /// A row the operator adds and leaves alone costs them nothing: <see cref="ToProject"/> drops it.
    /// </summary>
    public ObservableCollection<ProjectResourceRowViewModel> ResourceRows { get; } = [];

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
            // Resources only — never MemoryRef beside it (see Project.MemoryRef's own doc comment on why an
            // initializer must pick one: both write the same underlying list, and whichever is set last wins). Every
            // row the operator can see and edit is right here in ResourceRows now, Memory rows included, so there is
            // no second, hidden value left to fold in and no order to get wrong.
            Resources =
            [
                .. ResourceRows.Select(row => row.ToDomain()).Where(resource => !string.IsNullOrWhiteSpace(resource.Reference)),
            ],
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

    /// <summary>Drops the logo. The stored copy goes when the project is saved, not here — cancelling must leave it as it was.</summary>
    [RelayCommand]
    private void ClearLogo() => LogoSource = string.Empty;

    [RelayCommand]
    private void AddInfoField() => AdditionalInfo.Add(new ProjectInfoFieldViewModel());

    [RelayCommand]
    private void RemoveInfoField(ProjectInfoFieldViewModel field) => AdditionalInfo.Remove(field);

    /// <summary>Appends a blank resource row (AC-485), the same shape <see cref="AddInfoField"/> already has — Folder pre-selected for it the instant there is a picker at all, matching what <c>CreateAsync</c> does for a loaded row.</summary>
    [RelayCommand]
    private void AddResourceRow()
    {
        var row = new ProjectResourceRowViewModel(MemorySourceChoices);
        if (MemorySourceChoices.Count > 0)
        {
            row.SelectedMemorySourceChoice = MemorySourceChoices[0];
        }

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

    [RelayCommand]
    private void Clone() => CloneRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => CloseRequested?.Invoke(ToProject());

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>Re-running the portability check whenever the folder itself changes — a row already flagged machine-bound may no longer be once the operator points the project at a folder that now contains it, or the reverse.</summary>
    partial void OnSourceDirectoryChanged(string value) => _RefreshResourceDiagnostics();

    private void _AddResourceRow(ProjectResourceRowViewModel row)
    {
        ResourceRows.Add(row);
        _UpdateLastRowFlags();
        // Any change that affects what gets saved or how it is judged — Reference, Role, ReachesSessions, and which
        // memory source is picked.
        //
        // AC-485 review (FIX 6) — Role is included, but not for either reason this comment used to give: Role does
        // not gate the broken-reference probe (ReachesSessions does, see _RefreshResourceDiagnostics below), and it
        // does not gate portability either (ProjectResourcePathPortability.IsMachineBound never looks at a row's
        // Role at all). The real reason is MUST-FIX 1: switching a row's role away from Memory (or back to it) can
        // itself rewrite Reference's own text — see ProjectResourceRowViewModel.OnRoleChanged — and it is that
        // rewritten value the diagnostics below must judge, not whatever Reference held a moment before the switch.
        row.PropertyChanged += _OnResourceRowChanged;
    }

    /// <summary>
    /// Sets <see cref="ProjectResourceRowViewModel.IsLastRow"/> on every row (AC-485 review, FIX 8) — called
    /// whenever <see cref="ResourceRows"/> gains or loses a row, since which one is last can only change then.
    /// </summary>
    private void _UpdateLastRowFlags()
    {
        for (var i = 0; i < ResourceRows.Count; i++)
        {
            ResourceRows[i].IsLastRow = i == ResourceRows.Count - 1;
        }
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

    /// <summary>
    /// Completes once the most recently scheduled <see cref="_RefreshResourceDiagnostics"/> call has written its
    /// answer onto every row, or been superseded by a later one (AC-485 review, MUST-FIX 2) — a hook a test can
    /// await deterministically instead of sleeping for a computation whose entire point is to run off the UI
    /// thread. Production code never awaits this itself: what the operator sees update is each row's own bound
    /// <see cref="ProjectResourceRowViewModel.IsBroken"/>/<see cref="ProjectResourceRowViewModel.IsMachineBound"/>,
    /// whenever the background work gets there.
    /// </summary>
    internal Task ResourceDiagnosticsRefreshCompleted { get; private set; } = Task.CompletedTask;

    /// <summary>Bumped by every call to <see cref="_RefreshResourceDiagnostics"/> — see that method's remarks on why a stale answer must be told apart from the current one.</summary>
    private int _resourceDiagnosticsRefreshVersion;

    /// <summary>
    /// Schedules a re-run of <see cref="ProjectResourceProbe"/> and <see cref="ProjectResourcePathPortability"/>
    /// over every row without waiting for it, so the row-level property change that triggered this call (a
    /// keystroke in the Reference box, a role switch, adding or removing a row, the folder changing) returns to the
    /// UI immediately.
    /// <para>
    /// AC-485 review (MUST-FIX 2): this used to run the probe's I/O synchronously, on whatever thread called it —
    /// the UI thread for every trigger above. The <c>Reference</c> box binds with Avalonia's default per-keystroke
    /// trigger, so a row whose path check does not answer quickly — a disconnected mapped drive, say, which the
    /// probe's own UNC guard does not catch since a drive letter is not a UNC path — cost up to the probe's own
    /// 200 ms time budget on <em>every character typed</em>: measured at 204 ms/call, so 35 keystrokes cost roughly
    /// 7 seconds of a frozen window, for every row in the dialog at once. The actual I/O now runs on a pool thread
    /// (see <see cref="_RunResourceDiagnosticsAsync"/>) and only its answer is marshalled back.
    /// </para>
    /// </summary>
    private void _RefreshResourceDiagnostics(bool immediately = false) =>
        ResourceDiagnosticsRefreshCompleted = _RunResourceDiagnosticsAsync(
            immediately ? TimeSpan.Zero : ResourceDiagnosticsQuietPeriod);

    /// <summary>
    /// How long the typing has to stop before a row is judged. Long enough that writing a path straight through
    /// never triggers a check, short enough that the answer feels like it belongs to what was just typed. Opening
    /// the dialog passes <c>immediately</c> instead: there is nothing to wait for, and a row stored as broken should
    /// say so the moment it is on screen.
    /// </summary>
    private static readonly TimeSpan ResourceDiagnosticsQuietPeriod = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The actual work <see cref="_RefreshResourceDiagnostics"/> schedules (AC-485 review, MUST-FIX 2). Row objects
    /// are read only on the calling (UI) thread, into a plain snapshot — touching a
    /// <see cref="ProjectResourceRowViewModel"/> off the UI thread is not safe — and the snapshot alone, plain data
    /// with no UI affinity, is handed to <see cref="Task.Run{TResult}(Func{TResult})"/> for the part that can
    /// actually take a while: <see cref="ProjectResourceProbe.FindUnresolved"/> and
    /// <see cref="ProjectResourcePathPortability.IsMachineBound"/> over every row. Once that finishes, the answer is
    /// written back onto each row — but only if no newer call has started in the meantime: <c>version</c> is
    /// compared against <see cref="_resourceDiagnosticsRefreshVersion"/>'s current value, and a stale answer
    /// (a slow, earlier call finishing after a faster, later one already has) is simply dropped rather than
    /// overwriting the fresher one — a race that was not possible back when every call ran to completion
    /// synchronously, in the order it was made.
    /// <para>
    /// AC-485 review (FIX 3): <see cref="ProjectResourceProbe.FindUnresolved"/> itself never looks at
    /// <see cref="ProjectResource.ReachesSessions"/> for a row that is not switched off elsewhere — it filters the
    /// whole input by that flag before doing any I/O, then answers with the bare <em>text</em> of every reference it
    /// found missing. Matching that text back onto every row sharing it — including a row this probe was never
    /// asked about, because its own <c>ReachesSessions</c> was false — would call a row "broken" that was simply
    /// never judged. Gating the assignment itself on the row's own <c>ReachesSessions</c> (not only on the string
    /// being in the result) keeps "not judged" and "judged and broken" apart, which the doc comment on
    /// <see cref="ProjectResourceRowViewModel.IsBroken"/> already promises and the assignment below did not
    /// previously keep.
    /// </para>
    /// </summary>
    /// <summary>
    /// Cancelled and replaced at the start of every <see cref="_RunResourceDiagnosticsAsync"/> call (AC-503
    /// acceptance criterion 5) — a Reachability check is a network call that can run long enough to still be in
    /// flight when a newer edit supersedes it, unlike the filesystem probe's own version-guard (which only ever
    /// discards a stale <em>answer</em>, since a filesystem check finishes fast enough that letting it run to
    /// completion costs nothing). Cancelling the token itself, not merely discarding the result, is what actually
    /// stops an older in-flight tool call rather than leaving it to complete unread in the background.
    /// </summary>
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

        // Wait for the typing to stop before judging anything. Running the probe off the UI thread stops it freezing
        // the window, but it does not stop it running once per character — and a half-typed path is a path that does
        // not exist, so without this the row flashes "could not be found" in red while the operator is still writing
        // it. Superseded within the quiet period means this call never touches disk at all.
        //
        // Deliberately not solved by committing the box on focus loss instead, which is where this landed first: it
        // made the typed value reach the row only when focus moved, and saving straight from the box — typing a path
        // and going for the confirm button, the ordinary way to use this dialog — dropped the row entirely. Measured,
        // not reasoned: the test SavingStraightFromTheReferenceBox_KeepsWhatWasTyped fails against that version. What
        // is on screen is what is in the view model, here as everywhere else in this app; the cost of the check is
        // this delay's problem, not the binding's.
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
            var isMachineBound = resources.ToDictionary(
                pair => pair.row,
                pair => ProjectResourcePathPortability.IsMachineBound(sourceDirectory, pair.resource.Reference));
            return (Unresolved: unresolvedReferences, MachineBound: isMachineBound);
        });

        // AC-503: a Memory row whose picked source has a reachability check and a non-blank typed value gets one,
        // run alongside the filesystem probe above rather than after it — this is a network call and the two probes
        // judge disjoint sets of rows (this one only ever looks at Memory rows; ProjectResourceProbe never does,
        // see its own class remarks), so there is nothing for the two to race over.
        var reachabilityTasks = resources
            .Where(pair => pair.row.Role == ProjectResourceRole.Memory
                && pair.row.SelectedMemorySourceChoice?.CheckReachability is not null
                && !string.IsNullOrWhiteSpace(pair.row.Reference))
            .Select(pair => _RunReachabilityCheckAsync(
                pair.row,
                pair.row.Reference.Trim(),
                pair.row.SelectedMemorySourceChoice!.CheckReachability!,
                version,
                reachabilityCancellation.Token))
            .ToList();

        var (unresolved, machineBound) = await fsProbeTask.ConfigureAwait(true);
        await Task.WhenAll(reachabilityTasks).ConfigureAwait(true);

        if (version != _resourceDiagnosticsRefreshVersion)
        {
            return;
        }

        foreach (var (row, resource) in resources)
        {
            row.IsBroken = resource.ReachesSessions && unresolved.Contains(resource.Reference);
            row.IsMachineBound = machineBound[row];
        }
    }

    /// <summary>
    /// Runs one Memory row's own <see cref="ProjectMemorySourceRegistration.CheckReachability"/> (AC-503) and writes
    /// the answer back onto <paramref name="row"/> — but only if <paramref name="version"/> still matches
    /// <see cref="_resourceDiagnosticsRefreshVersion"/> once the check completes, the same stale-answer guard
    /// <see cref="_RunResourceDiagnosticsAsync"/> already applies to the filesystem probe's own result.
    /// </summary>
    /// <param name="value">The bare value the operator typed — never the folded <c>"{scheme}:{value}"</c> form <see cref="ProjectResourceRowViewModel.ToDomain"/> saves, which the plugin's own check never asked to see.</param>
    /// <param name="check">The row's picked source's own check delegate.</param>
    /// <param name="version">The refresh version this call belongs to.</param>
    /// <param name="cancellationToken">Cancelled by a newer <see cref="_RunResourceDiagnosticsAsync"/> call starting (see <see cref="_reachabilityCancellation"/>) — never awaited past that point.</param>
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
        catch (Exception)
        {
            // A plugin's own check delegate threw before it could decide anything — treated exactly like a network
            // failure at the host-probe layer (Cockpit.Plugins.Abstractions.Mcp.McpProbeOutcome.Failed): never
            // NotFound for a failure this ambiguous (AC-503 acceptance criterion 4), which would name the wrong
            // cause for what might simply be a hiccup in the plugin's own check.
            result = ProjectMemorySourceReachabilityResult.NotSignedIn;
        }

        if (version != _resourceDiagnosticsRefreshVersion)
        {
            return;
        }

        row.Reachability = result.State;
        row.ReachabilityDetail = result.State == ProjectMemorySourceReachability.Confirmed ? result.Detail : null;
    }

    private static string? _NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
