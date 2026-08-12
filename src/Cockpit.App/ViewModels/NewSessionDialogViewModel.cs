using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Core.WorkingPaths;
using Cockpit.Core.Worktrees;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// Backs the New-session dialog (#31/#17/#15/#32/#44): pick the session kind (SDK vs TTY), a profile, and
// its start mode/model/effort. Choosing a profile loads its saved defaults (`ProfileDefaults`),
// which the operator can still override before starting. Mode here offers all four modes including
// bypass, since bypass is launch-only and this dialog is the one place it can be chosen. Also lets the
// operator uncheck individual MCP servers from the shared registry for just this session (#44) via
// `McpServers`. The view closes via `CloseRequested`, carrying the choices on
// confirm or null on cancel.
// `SelectedKind` plus the `IsSdk`/`IsTty` computed pair is the
// mechanism for showing/hiding kind-specific fields: today only the hint text differs (see
// `NewSessionDialog.axaml`), but a later kind-specific field just adds another
// `IsVisible="{Binding IsSdk}"`/`IsTty` binding without touching the rest of the dialog.
public partial class NewSessionDialogViewModel : ViewModelBase
{
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly IProfileLoginStarter? _loginStarter;
    private readonly ISessionProfileStore? _profileStore;
    private readonly IMcpServerCatalog? _mcpServerCatalog;
    private readonly IMcpToolTokenEstimator? _tokenEstimator;
    private readonly IMcpOAuthCoordinator? _oauthCoordinator;
    private CancellationTokenSource? _tokenEstimateCts;
    private readonly IWorkingPathHistoryStore? _workingPathStore;
    private readonly ConversationPickerRegistration? _conversationPicker;
    private readonly ITtySessionProviderResolver? _ttyProviderResolver;
    private readonly IPluginTtyProviderRegistry? _ttyProviderRegistry;
    private readonly IPluginProviderRegistry? _sessionProviderRegistry;
    private readonly IWorktreeManager? _worktreeManager;
    private readonly IProjectStore? _projectStore;

    // The registered memory sources (AC-165/166), mapped once at construction to the plain Core type
    // `SessionStartDefaults.Resolve` reads, rather than per call — the registry's contents do not
    // change over the dialog's lifetime, and three call sites below would otherwise each repeat the mapping.
    private readonly IReadOnlyList<ProjectMemorySource>? _memorySources;
    private WorkingPathHistory _history = WorkingPathHistory.Empty;
    private CancellationTokenSource? _launchOptionsRefreshCts;
    private CancellationTokenSource? _repoDetectCts;

    // Whether the operator has set the working directory themselves (typed, browsed, cloned, resumed, or prefilled) —
    // after which a profile's default folder no longer replaces it (AC-130). Sticky defaults: switching profiles keeps
    // filling the folder from the new profile's default until the operator touches it, then their choice stands, so a
    // profile re-select (including the Manage-profiles round-trip that reloads the dialog) never silently wipes it.
    private bool _workingDirectoryTouched;

    // Set while a profile's default folder is being applied, so that programmatic set is not mistaken for the operator touching the field.
    private bool _applyingProfileWorkingDirectory;

    // Whether the operator has typed a session name themselves — after which picking a project no longer fills it with the project's name.
    private bool _sessionNameTouched;

    // Set while a project's name is being applied, so that programmatic set is not mistaken for the operator typing one.
    private bool _applyingProjectSessionName;

    // Whether the operator has changed the MCP checklist themselves — after which a profile switch no longer re-applies the profile's pre-selection over their ticks (AC-130).
    private bool _mcpSelectionTouched;

    // Set while a profile's MCP pre-selection is being applied, so re-ticking the checklist is not mistaken for the operator toggling it.
    private bool _applyingMcpSelection;

    // The configs `McpServers` was last built from (AC-736), kept because a row carries only a name while the project's pre-selection is answered per config.
    private IReadOnlyList<McpServerConfig> _offeredServers = [];

    // True while the checklist is being rebuilt for a newly chosen project — Start waits for it (`CanStart`).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRebuildingMcpChecklist;

    // Set while a profile switch is settling its kind, so the kind change it forces does not itself trigger a
    // dynamic-options refresh — the profile switch fires exactly one refresh at its end, for whatever kind won.
    private bool _suppressDynamicOptionsRefresh;

    // How long the background model/list refresh may run before the dialog gives up and keeps the declared options.
    private static readonly TimeSpan _LaunchOptionsRefreshTimeout = TimeSpan.FromSeconds(8);

    // The in-flight background refresh of the active kind's launch options (Codex's model/list), so a test can await it. Completed when none is running.
    internal Task LaunchOptionsRefresh { get; private set; } = Task.CompletedTask;

    // The in-flight rebuild of the MCP checklist after a project switch. Exposed so a caller that selects a project
    // before showing the dialog can wait for it — otherwise the dialog can be on screen, and startable, while the
    // checklist still shows the servers of the project it was not opened on.
    internal Task McpChecklistRefresh { get; private set; } = Task.CompletedTask;

    // Raised when the dialog should close: the result carries the confirmed choices, or null on cancel.
    public event Action<NewSessionResult?>? CloseRequested;

    // Raised when the operator wants to manage profiles; the host opens the Manage-profiles dialog and reloads.
    public event Action? ManageProfilesRequested;

    // Raised when the operator picks "Clone from a Git URL…" (AC-90); the host opens the clone dialog and, on success, sets `WorkingDirectory` to the local clone path.
    public event Action? CloneFromUrlRequested;

    // The label of the special quick-pick entry that opens the clone-from-URL flow instead of choosing a folder (AC-90).
    public const string CloneFromUrlLabel = "Clone from a Git URL…";

    // Which kind of session to create; chosen in the dialog itself (#32), defaulting to TTY (Raymond's preferred default).
    [ObservableProperty]
    private SessionKind _selectedKind = SessionKind.Tty;

    public bool IsSdk => SelectedKind == SessionKind.Sdk;

    public bool IsTty => SelectedKind == SessionKind.Tty;

    // Window title, tracking the chosen kind.
    public string HeaderText => IsSdk ? "New session" : "New session (TTY)";

    // The mode/model/effort options are Claude-CLI concepts, shown only for a Claude profile — TTY passes
    // them as launch-only CLI flags, SDK keeps them live-switchable. A local provider has none of these,
    // so the whole block is hidden for it (#26).
    // The legacy typed permission/model/effort block is retired: Claude renders its options through the generic
    // plugin-option rows now, like every provider. Kept false until that block and SessionOptionCatalog are removed.
    public bool ShowSessionOptions => false;

    // The SDK "stays live-switchable" hint, shown only for a Claude SDK session (a local session has no such dropdowns).
    public bool ShowSdkStartHint => IsSdk && IsClaudeProfile;

    // The TTY "start defaults only" hint, shown only for a Claude TTY session.
    public bool ShowTtyStartHint => IsTty && IsClaudeProfile;

    public ObservableCollection<SessionProfile> Profiles { get; } = [];

    // The projects to start under (AC-163); empty for an operator who has made none, which hides the picker.
    public ObservableCollection<Project> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    // The project this session works on, or null for a session belonging to none — how the cockpit has always
    // started one. Picking one pre-fills the folder, profile, worktree choice and MCP checklist through
    // `SessionStartDefaults`; every one of them stays editable below.
    [ObservableProperty]
    private Project? _selectedProject;

    // The shared registry's enabled MCP servers (#44), each with its own checkbox so the operator can opt
    // individual ones out of just this session. Defaults to all checked, matching the pre-#44 behaviour.
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    // Whether the MCP checklist is shown at all — hidden when the registry has no enabled servers.
    public bool HasMcpServers => McpServers.Count > 0;

    // Whether a *selected* server needs a sign-in it does not have (AC-355) — the dialog says so, but it
    // never blocks Start over it: the operator may well know it and mean to proceed without that server's tools, or
    // sign in from a session that is already running. Follows the same non-blocking inline-hint pattern as
    // `ShowLoginHint` rather than a confirmation dialog of its own, so it is visible well before Start
    // is ever pressed — an unticked server (its tools already opted out of this session) says nothing here.
    public bool ShowMcpAuthorizationHint =>
        McpServers.Any(server => server.IsEnabledForSession && server.AuthState == McpAuthState.AuthorizationRequired);

    // The AC-355 hint text, naming every ticked server that still needs a sign-in.
    public string McpAuthorizationHintText
    {
        get
        {
            var names = McpServers
                .Where(server => server.IsEnabledForSession && server.AuthState == McpAuthState.AuthorizationRequired)
                .Select(server => server.Name)
                .ToList();

            if (names.Count == 0)
            {
                return string.Empty;
            }

            var list = string.Join(", ", names);
            return names.Count == 1
                ? $"{list} needs a sign-in — its tools won't be available until you sign in from the MCP servers dialog."
                : $"{list} need a sign-in — their tools won't be available until you sign in from the MCP servers dialog.";
        }
    }

    // All four modes — including the launch-only bypass — since this dialog is the one place bypass can be chosen.
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    // The three SDK reading levels (AC-138) offered by the override picker, shown only for an SDK session (`IsSdk`).
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    // The reading level this SDK session will open with (AC-138). Seeded from the selected profile's default view
    // (`OnSelectedProfileChanged`) and overridable here; only meaningful for an SDK session, so the
    // picker is hidden for a TTY one. Carried into `NewSessionResult` on confirm.
    [ObservableProperty]
    private ReadingLevelOption _selectedReadingLevel = SessionOptionCatalog.DefaultReadingLevel;

    // Optional friendly name for the session, shown in the sidebar and above the panel; blank falls back to "&lt;profile&gt; - N".
    [ObservableProperty]
    private string _sessionName = string.Empty;

    // The first prompt a plugin pre-filled the dialog with (`NewSessionPrefill.InitialPrompt`), shown read-only
    // so the operator reads the text before it reaches an agent. The dialog is the confirmation gate for a prefill,
    // and this is the one field whose contents can come from outside the cockpit entirely — an issue body written by
    // a stranger — so it is the last field that should be invisible on it. Blank for an operator-opened dialog.
    [ObservableProperty]
    private string _initialPrompt = string.Empty;

    // Only a pre-filled prompt has anything to show; an operator who opened the dialog themselves has no prompt yet.
    public bool HasInitialPrompt => !string.IsNullOrWhiteSpace(InitialPrompt);

    partial void OnInitialPromptChanged(string value) => OnPropertyChanged(nameof(HasInitialPrompt));

    // Whether this session picks up an earlier conversation instead of starting fresh — the answer to "the app
    // crashed and I want to go on where I was". Only the Claude CLI keeps a history to resume from, so the
    // controls are hidden for a local provider rather than offered and then ignored.
    [ObservableProperty]
    private SessionResumeMode _resumeMode = SessionResumeMode.New;

    // The conversation to resume when `ResumeMode` is `SessionResumeMode.BySessionId` — the id shown in a session's header, or copied from the transcript-search plugin.
    [ObservableProperty]
    private string _resumeSessionId = string.Empty;

    // Resuming is a Claude-CLI capability; a local provider keeps no conversation history of its own.
    public bool ShowResumeOptions => IsClaudeProfile;

    public bool IsStartingFresh => ResumeMode == SessionResumeMode.New;

    public bool IsResumingMostRecent => ResumeMode == SessionResumeMode.MostRecent;

    public bool IsResumingBySessionId => ResumeMode == SessionResumeMode.BySessionId;

    partial void OnResumeModeChanged(SessionResumeMode value)
    {
        OnPropertyChanged(nameof(IsStartingFresh));
        OnPropertyChanged(nameof(IsResumingMostRecent));
        OnPropertyChanged(nameof(IsResumingBySessionId));
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnResumeSessionIdChanged(string value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private void StartFreshConversation() => ResumeMode = SessionResumeMode.New;

    [RelayCommand]
    private void ContinueMostRecentConversation() => ResumeMode = SessionResumeMode.MostRecent;

    [RelayCommand]
    private void ResumeConversationById() => ResumeMode = SessionResumeMode.BySessionId;

    // Whether a plugin offers a way to browse the provider's conversation history — no picker, no search button (and the id can still be typed).
    public bool HasConversationPicker => _conversationPicker is not null;

    // Tooltip for the search button: whatever the picker calls itself, e.g. "Search transcripts".
    public string ConversationPickerTitle => _conversationPicker?.Title ?? string.Empty;

    // Runs the plugin's picker and fills in whatever conversation the operator chose. Cancelling leaves the field as it was.
    [RelayCommand]
    private async Task PickConversationAsync()
    {
        if (await _PickConversationAsync() is { SessionId.Length: > 0 } picked)
        {
            ResumeSessionId = picked.SessionId;
            ResumeMode = SessionResumeMode.BySessionId;

            // A session's transcript lives under the folder it ran in, so resuming it anywhere else would not
            // find it — start the resumed session in that folder rather than in whatever the operator last used.
            if (!string.IsNullOrWhiteSpace(picked.WorkingDirectory))
            {
                WorkingDirectory = picked.WorkingDirectory.Trim();
            }
        }
    }

    // Prefer the location-aware picker so a resumed session can start where it ran; fall back to the id-only form
    // for a picker that does not know the directory. Null when there is no picker or the operator cancelled.
    private async Task<PickedConversation?> _PickConversationAsync()
    {
        if (_conversationPicker is null)
        {
            return null;
        }

        if (_conversationPicker.PickWithLocationAsync is { } pickWithLocation)
        {
            return await pickWithLocation();
        }

        return await _conversationPicker.PickAsync() is { Length: > 0 } sessionId
            ? new PickedConversation(sessionId)
            : null;
    }

    // The choice as the session layer consumes it; a blank id falls back to a fresh conversation rather than a broken resume.
    private SessionResume _Resume() => ResumeMode switch
    {
        SessionResumeMode.MostRecent => SessionResume.MostRecent,
        SessionResumeMode.BySessionId when !string.IsNullOrWhiteSpace(ResumeSessionId) => SessionResume.BySessionId(ResumeSessionId.Trim()),
        _ => SessionResume.New,
    };

    // Optional per-session working directory (#: project-folder launch): the directory `claude` is
    // started in for this session, overriding the global option. Blank keeps the global default. Pre-fillable
    // from `RecentPaths`/`FavoritePaths` so a previously-used folder is one click away.
    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    // Remembered working directories for the quick-pick — favorites first (★), then recents — so a folder used before is one selection away.
    public ObservableCollection<RememberedPathOption> RememberedPaths { get; } = [];

    // Bound to the quick-pick ComboBox; selecting an entry fills `WorkingDirectory` and resets to null so it behaves as a picker, not a persistent selection.
    [ObservableProperty]
    private RememberedPathOption? _selectedRememberedPath;

    // Whether there is anything to offer in the folder quick-pick.
    public bool HasRememberedPaths => RememberedPaths.Count > 0;

    // Whether the currently-typed working directory is pinned — drives the favorite toggle's icon.
    public bool IsWorkingDirectoryFavorite => _history.IsFavorite(WorkingDirectory);

    // Filled star when the current folder is a favorite, outline otherwise — the toggle button's icon.
    public MaterialIconKind FavoriteToggleGlyph => IsWorkingDirectoryFavorite ? MaterialIconKind.Star : MaterialIconKind.StarOutline;

    // Whether the favorite toggle is actionable (there is a path to pin).
    public bool CanFavoriteWorkingDirectory => !string.IsNullOrWhiteSpace(WorkingDirectory);

    // Whether this session runs in its own git worktree on a dedicated branch (AC-85) — a per-session choice made here, next to the folder, not a profile setting. Only actionable when the folder is a git repository; forced off otherwise so it is never a silent no-op.
    [ObservableProperty]
    private bool _isolateInWorktree;

    // Whether the current working directory is a git repository — the reactive gate (§4) for offering worktree isolation.
    [ObservableProperty]
    private bool _isWorkingDirectoryGitRepo;

    // The branch the current working directory is on, when it is a git repository — the base a worktree would branch from, shown in the isolation status line.
    [ObservableProperty]
    private string? _workingDirectoryBaseBranch;

    // Whether the isolation checkbox is actionable (the folder is a git repository).
    public bool CanIsolateInWorktree => IsWorkingDirectoryGitRepo;

    // The status line under the isolation checkbox: a repo shows the base branch it would fork from, a non-repo says why the option is disabled (§4).
    public string WorktreeStatusText => IsWorkingDirectoryGitRepo
        ? $"✓ Git repo{(string.IsNullOrEmpty(WorkingDirectoryBaseBranch) ? string.Empty : $" · base {WorkingDirectoryBaseBranch}")} → runs on a new cockpit/… branch"
        : "Pick a Git repository to enable isolation";

    // Whether to show the isolation control — only once the chosen folder is actually a git repository (Raymond
    // 2026-07-19: appear when it applies, not greyed-out beforehand). Also gated to a process-spawning provider: a
    // local HTTP provider (Ollama/LM Studio) spawns no process and so has no working tree to isolate.
    public bool ShowWorktreeIsolation => SelectedProfile is not null && !IsLocalProfile && IsWorkingDirectoryGitRepo;

    // Each working-directory change supersedes the last detection. A manager-less design-time VM reports no repo, so
    // the option simply never enables in the previewer.
    private async Task _DetectWorkingDirectoryRepoAsync(string directory)
    {
        _repoDetectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _repoDetectCts = cts;

        GitRepositoryInfo? info = null;
        if (_worktreeManager is not null && !string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                // Task.Run so the synchronous git spawn prefix never runs on the UI thread; ConfigureAwait(true) keeps
                // the continuation on it, since it mutates bound properties.
                info = await Task.Run(() => _worktreeManager.DetectRepositoryAsync(directory.Trim(), cts.Token), cts.Token).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A folder that vanished, or git being unavailable, is "not a repository here" — the option disables,
                // it does not fail the dialog.
                info = null;
            }
        }

        if (!ReferenceEquals(_repoDetectCts, cts))
        {
            return;
        }

        IsWorkingDirectoryGitRepo = info is not null;
        WorkingDirectoryBaseBranch = info?.CurrentBranch;
        if (!IsWorkingDirectoryGitRepo && IsolateInWorktree)
        {
            IsolateInWorktree = false;
        }

        OnPropertyChanged(nameof(CanIsolateInWorktree));
        OnPropertyChanged(nameof(WorktreeStatusText));
    }

    partial void OnIsWorkingDirectoryGitRepoChanged(bool value)
    {
        OnPropertyChanged(nameof(CanIsolateInWorktree));
        OnPropertyChanged(nameof(WorktreeStatusText));
        OnPropertyChanged(nameof(ShowWorktreeIsolation));
    }

    [ObservableProperty]
    private SessionProfile? _selectedProfile;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    // The Claude model for this session, as free text with suggestions — an alias, or a pinned model/snapshot.
    [ObservableProperty]
    private string _selectedClaudeModel = SessionOptionCatalog.DefaultModel.Value;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    [ObservableProperty]
    private bool _isSelectedProfileLoggedIn;

    // Config directory of the selected profile, shown under the picker so it is clear where its login lives.
    public string? SelectedProfileConfigDir => SelectedProfile?.Claude?.ConfigDir;

    // Whether the selected profile runs on Claude (login gating, resume, the login hint apply) — true whether it
    // still carries a legacy `ClaudeConfig` or, after Fase 4, the bundled Claude provider plugin's
    // config. A profile's `SessionProfile.Claude` is non-null in both cases.
    public bool IsClaudeProfile => SelectedProfile?.Claude is not null;

    // A local OpenAI-compatible provider (Ollama/LM Studio) — no login, no TUI, no resume.
    public bool IsLocalProfile => SelectedProfile?.Provider is SessionProvider.Ollama or SessionProvider.LmStudio;

    // Whether the selected profile has a TUI to run at all — the gate for offering the Kind picker and for
    // what TTY actually launches. Claude always does (its own `claude` TTY provider); a plugin profile
    // does only when it registered one via `ICockpitHost.AddTtyProvider` under the same provider id its
    // session provider uses (#45 fase B2) — resolved the same way `TtyViewModel` resolves it at
    // launch, so the dialog never offers a kind the launch would then refuse. A local HTTP provider (Ollama/
    // LM Studio) is never a program a terminal can host, so it has none either way.
    public bool HasTtyProvider => SessionKindDefaults.HasTtyRoute(SelectedProfile, _ttyProviderResolver);

    // The declared start defaults for the selected profile's plugin TTY provider (Codex's sandbox policy, say) — empty for Claude/local profiles or a plugin with none declared.
    public ObservableCollection<PluginTtyOptionSelectionViewModel> PluginTtyOptions { get; } = [];

    public bool HasPluginTtyOptions => PluginTtyOptions.Count > 0;

    // Shown when TTY is chosen for a plugin profile (Claude or Codex) that declared its own start defaults.
    public bool ShowPluginTtyOptions => IsTty && HasPluginTtyOptions;

    // The declared per-session start defaults for the selected profile's SDK session provider (Codex's sandbox/model) — empty for Claude/local profiles or a provider with none declared. Reuses the same generic option row as the TTY route.
    public ObservableCollection<PluginTtyOptionSelectionViewModel> SdkLaunchOptions { get; } = [];

    public bool HasSdkLaunchOptions => SdkLaunchOptions.Count > 0;

    // Shown when SDK is chosen for a plugin profile (Claude or Codex) that declared its own start defaults — the SDK mirror of `ShowPluginTtyOptions`.
    public bool ShowSdkLaunchOptions => IsSdk && HasSdkLaunchOptions;

    // Provider label shown next to the picker; empty for Claude, which needs no badge.
    public string SelectedProviderLabel => IsLocalProfile ? SessionProviderCatalog.Resolve(SelectedProfile!.Provider).Label : string.Empty;

    public string LoginStatusLabel => IsSelectedProfileLoggedIn ? "logged in" : "not logged in";

    // AC-713: whether the *selected* profile's provider declared a login at all, not just "not local" — a
    // gate-less provider (Gemini, Kimi) reads `IsSelectedProfileLoggedIn == true` and must not look logged in.
    public bool HasLoginConcept => SelectedProfile is { } profile && (_loginStarter?.CanStartLogin(profile) ?? false);

    // AC-713: an SDK session that is not logged in — a TTY logs in via its own TUI, generalized off `IsClaudeProfile` now that Codex has a gate too.
    public bool ShowLoginHint => IsSdk && !IsLocalProfile && SelectedProfile is not null && !IsSelectedProfileLoggedIn;

    // AC-713: the in-app sign-in this hint's "Login" button starts, rendered the same way everywhere it can start.
    [ObservableProperty]
    private LoginFlowRowViewModel? _loginFlow;

    public bool HasLoginFlow => LoginFlow is not null;

    partial void OnLoginFlowChanged(LoginFlowRowViewModel? value) => OnPropertyChanged(nameof(HasLoginFlow));

    public bool CanStartLogin => HasLoginConcept;

    [RelayCommand(CanExecute = nameof(CanStartLogin))]
    private void Login()
    {
        if (SelectedProfile is not { } profile || _loginStarter?.StartLogin(profile, CancellationToken.None) is not { } flow)
        {
            return;
        }

        var loginFlow = new LoginFlowRowViewModel(flow);
        loginFlow.Completed = succeeded =>
        {
            if (succeeded)
            {
                IsSelectedProfileLoggedIn = true;
            }
        };
        LoginFlow = loginFlow;
    }

    public string LoginStatusBrushKey => IsSelectedProfileLoggedIn
        ? "CockpitStatusDoneBrush"
        : "CockpitStatusWaitingBrush";

    // A selected profile is startable, except a Claude *SDK* session, which is gated on login: an
    // SDK spawn talks to the CLI headlessly and would just fail unauthenticated. A TTY session hosts the
    // real interactive TUI, which runs its own `/login`, so it needs no pre-check; a local profile
    // has no login at all.
    // Resuming by id with no id typed is not a session anyone asked for, so Start stays disabled until it is filled in.
    public bool CanStart =>
        SelectedProfile is not null
        && (IsLocalProfile || IsTty || IsSelectedProfileLoggedIn)
        && (ResumeMode != SessionResumeMode.BySessionId || !string.IsNullOrWhiteSpace(ResumeSessionId))
        // A project switch rebuilds the checklist off disk, and Start reads that checklist. Pressed inside that
        // window it would carry the previous project's ticks into the new project's session — the folder and the
        // id from one, the servers from the other. The caller that opens the dialog on a project already waits for
        // this; the operator switching project inside the dialog had nothing holding them back.
        && !IsRebuildingMcpChecklist;

    // Design-time constructor for the Avalonia previewer: one logged-in profile so the dialog renders.
    public NewSessionDialogViewModel()
    {
        var personal = new SessionProfile("personal", new ClaudeConfig("~/.claude-personal"), Purpose: "private");
        Profiles.Add(personal);
        SelectedProfile = personal;
        IsSelectedProfileLoggedIn = true;
    }

    public NewSessionDialogViewModel(
        ISessionProfileStore profileStore,
        IProfileLoginChecker loginChecker,
        IMcpServerCatalog? mcpServerCatalog = null,
        IWorkingPathHistoryStore? workingPathStore = null,
        IConversationPickerRegistry? conversationPickers = null,
        ITtySessionProviderResolver? ttyProviderResolver = null,
        IPluginTtyProviderRegistry? ttyProviderRegistry = null,
        IPluginProviderRegistry? sessionProviderRegistry = null,
        IWorktreeManager? worktreeManager = null,
        IMcpToolTokenEstimator? tokenEstimator = null,
        IProjectStore? projectStore = null,
        IMcpOAuthCoordinator? oauthCoordinator = null,
        IProjectMemorySourceRegistry? memorySourceRegistry = null,
        IProfileLoginStarter? loginStarter = null)
    {
        _projectStore = projectStore;
        _memorySources = memorySourceRegistry?.Sources.ToMemorySources();
        _conversationPicker = conversationPickers?.Pickers.FirstOrDefault();
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _loginStarter = loginStarter;
        _mcpServerCatalog = mcpServerCatalog;
        _tokenEstimator = tokenEstimator;
        _workingPathStore = workingPathStore;
        _ttyProviderResolver = ttyProviderResolver;
        _ttyProviderRegistry = ttyProviderRegistry;
        _sessionProviderRegistry = sessionProviderRegistry;
        _worktreeManager = worktreeManager;
        _oauthCoordinator = oauthCoordinator;

        // AC-713: Cancel/Confirm both raise this — a running flow's subprocess must not outlive the dialog that
        // started it just because the operator closed the window instead of switching profiles (which already
        // disposes it via `OnLoginFlowChanging`).
        CloseRequested += result => LoginFlow?.DisposeAsync().AsTask();
    }

    // Loads the profiles and selects the first, so the dialog opens ready to confirm. Also loads the enabled MCP
    // servers (#44) into the checklist, all pre-checked — from the catalog, so a plugin's own servers (AC-11) are
    // offered and uncheckable here alongside the registry's.
    public async Task LoadAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        var profiles = await _profileStore.LoadAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault();

        // The projects to choose from (AC-163). Loaded before the checklist because picking one changes which
        // servers are offered — a project's overlay decides what exists for its sessions.
        if (_projectStore is not null)
        {
            var projects = await _projectStore.LoadAsync();
            Projects.Clear();
            foreach (var project in projects.Projects)
            {
                Projects.Add(project);
            }

            OnPropertyChanged(nameof(HasProjects));
        }

        await _PopulateMcpServersAsync();

        if (_workingPathStore is not null)
        {
            _history = await _workingPathStore.LoadAsync();
            _RefreshRememberedPaths();
        }
    }

    // (Re)builds the MCP checklist for the selected project (AC-163). Rebuilt rather than merely re-ticked,
    // because a project can bring servers of its own — but it never takes one away: every server the registry
    // offers stays listed whichever project is picked, and the project's choice shows as the ticks.
    private async Task _PopulateMcpServersAsync()
    {
        if (_mcpServerCatalog is null)
        {
            return;
        }

        IsRebuildingMcpChecklist = true;
        ConfirmCommand.NotifyCanExecuteChanged();
        try
        {
            await _RebuildMcpServersAsync();
        }
        finally
        {
            IsRebuildingMcpChecklist = false;
            ConfirmCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task _RebuildMcpServersAsync()
    {
        var registry = await _mcpServerCatalog!.GetServersForProjectAsync(SelectedProject?.Id);

        // What the operator ticked, kept across the rebuild for the servers that survive it. Without this their own
        // edits are gone — every fresh row starts ticked — while _mcpSelectionTouched keeps the profile's saved
        // selection from being re-applied, so switching project after one manual untick turned everything back on.
        // First row wins on a repeated name, rather than throwing: two servers may share one (nothing between "Add
        // server" and the store forbids it), and losing a tick is a smaller failure than a dialog that cannot open.
        var ticked = _mcpSelectionTouched
            ? McpServers
                .GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().IsEnabledForSession, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var existing in McpServers)
        {
            existing.PropertyChanged -= _OnMcpServerToggled;
        }

        var offered = McpServerRegistryFilter.OfferedToOperator(registry);
        _offeredServers = offered;

        McpServers.Clear();
        foreach (var server in offered)
        {
            // Set before subscribing, so restoring a tick does not read as the operator making one.
            var item = new McpServerSelectionItemViewModel(server.Name)
            {
                IsEnabledForSession = ticked?.GetValueOrDefault(server.Name, true) ?? true,
            };
            item.PropertyChanged += _OnMcpServerToggled;
            McpServers.Add(item);
        }

        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(HasMcpTokenSummary));
        OnPropertyChanged(nameof(McpToolTokenSummary));

        // AC-355: a storage-only read (no network, no browser — see IMcpOAuthCoordinator.GetStateAsync), so unlike
        // the tool-token estimate below it is awaited here rather than backgrounded: the checklist rebuild this sits
        // inside already gates Start (IsRebuildingMcpChecklist), and this finishes fast enough not to matter.
        await _RefreshMcpAuthStatesAsync(offered);

        // Pre-flight tool-token estimate (AC-134): enumerate each server's tools in the background and roll the
        // ticked ones into a running total, so the operator sees roughly what the selection costs before starting.
        _ = _EstimateMcpTokensAsync(refresh: false);

        // The selected profile was set before this list existed, so its pre-selection (AC-130) could not apply yet —
        // apply it now the checklist is populated, unless the operator has already edited it. On a later profile
        // switch OnSelectedProfileChanged does the same against the already-built list.
        if (!_mcpSelectionTouched)
        {
            _ApplyProfileMcpSelection();
        }
    }

    // Reads each offered server's OAuth standing (AC-355) and writes it onto the checklist row in the same position
    // — a no-op without a coordinator (design-time/no service) so the checklist behaves exactly as before AC-355
    // when nobody asked for status.
    private async Task _RefreshMcpAuthStatesAsync(IReadOnlyList<McpServerConfig> offered)
    {
        if (_oauthCoordinator is null)
        {
            return;
        }

        // Paired by position: the rows were built from this same list in this same order, and two servers may share a
        // name — keying on it threw on the way into the dialog, so the dialog did not open at all.
        // Snapshotted before the first await, because the loop awaits per server while a project switch rebuilds the
        // collection without waiting. Reading it live would write one list's status onto another list's row — a
        // sign-in badge on the wrong server, silently. Writing to rows nobody looks at any more is the harmless end.
        var rows = McpServers.ToList();
        for (var index = 0; index < offered.Count && index < rows.Count; index++)
        {
            var server = offered[index];
            var item = rows[index];

            McpAuthState state;
            try
            {
                state = await _oauthCoordinator.GetStateAsync(server).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A status read is informational; a storage hiccup leaves the row as it was rather than becoming a
                // false "sign-in needed".
                continue;
            }

            // NotRequired is not worth carrying onto the row — the checklist's own "?" tooltip already covers
            // every other reason a server can't be counted, and only AuthorizationRequired changes what it says.
            item.AuthState = state == McpAuthState.NotRequired ? null : state;
        }

        OnPropertyChanged(nameof(ShowMcpAuthorizationHint));
        OnPropertyChanged(nameof(McpAuthorizationHintText));
    }

    // Applies a chosen project to the dialog (AC-163). The profile is selected first so its own defaults land, then
    // the project's values are written over them — that ordering *is* the precedence rule
    // (`SessionStartDefaults`): the project overrides, the profile falls back, and a field the operator
    // already touched is left alone either way.
    // The chosen project's description, as a flat value rather than a path through `SelectedProject`.
    // A binding that walks into a null object yields no value at all, and an `IsVisible` left with no value
    // falls back to visible — which is how the hint under the picker held an empty line open, spacing the Project
    // row away from the Profile row whenever no project was chosen.
    public string? SelectedProjectDescription => SelectedProject?.Description;

    partial void OnSelectedProjectChanged(Project? value)
    {
        OnPropertyChanged(nameof(SelectedProjectDescription));

        // A session started on a project opens under that project's name (Raymond, 2026-08-01) rather than
        // "<profile> - N" — typing the name the row you right-clicked already carries is the same answer twice.
        // Sticky like the folder below: switching projects keeps refilling it until the operator types their own,
        // after which it is theirs; clearing the project clears our fill back out with it.
        if (!_sessionNameTouched)
        {
            _applyingProjectSessionName = true;
            try
            {
                SessionName = value?.Name ?? string.Empty;
            }
            finally
            {
                _applyingProjectSessionName = false;
            }
        }

        if (value?.DefaultProfileLabel is { Length: > 0 } label
            && Profiles.FirstOrDefault(profile => string.Equals(profile.Label, label, StringComparison.OrdinalIgnoreCase)) is { } matched)
        {
            SelectedProfile = matched;
        }

        var defaults = SessionStartDefaults.Resolve(value, SelectedProfile, memorySources: _memorySources);

        if (!_workingDirectoryTouched && defaults.WorkingDirectory is { Length: > 0 } directory)
        {
            _applyingProfileWorkingDirectory = true;
            try
            {
                WorkingDirectory = directory;
            }
            finally
            {
                _applyingProfileWorkingDirectory = false;
            }
        }

        // Only ever turns isolation on: the project asking for it is a default, and a repository check still gates it
        // (OnIsWorkingDirectoryGitRepoChanged switches it back off for a non-repository folder).
        if (defaults.IsolateInWorktree)
        {
            IsolateInWorktree = true;
        }

        McpChecklistRefresh = _PopulateMcpServersAsync();
    }

    // Ticks the MCP checklist to match the selected profile's saved pre-selection (AC-130): a null restriction ticks
    // every server (the pre-AC-130 default), a non-null set ticks exactly the servers it names. A no-op until the
    // checklist has been populated (LoadAsync), which then re-applies it for the profile selected during the load.
    private void _ApplyProfileMcpSelection()
    {
        if (McpServers.Count == 0)
        {
            return;
        }

        // A project's choice beats the profile's (Raymond, 2026-07-24): where a project says which servers it works
        // with, that is the answer, and the profile's saved selection is what a session started without one gets.
        if (SelectedProject is { } project)
        {
            // Resolved off the configs the checklist was built from, not off the rows: a server the project's own
            // memory reference brought in ticks itself (AC-736), and only the config carries that.
            var ticked = _offeredServers
                .Where(server => project.McpOverlay.IsSelectedByDefault(server))
                .Select(server => server.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _ApplyMcpSelection(server => ticked.Contains(server.Name));
            return;
        }

        var restriction = SelectedProfile?.EnabledMcpServerNames is { } names
            ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
            : null;

        _ApplyMcpSelection(server => restriction?.Contains(server.Name) ?? true);
    }

    // Ticks the checklist by `isSelected`, guarded so the re-ticking is not mistaken for the
    // operator editing it — which would freeze the checklist against the next project or profile switch.
    private void _ApplyMcpSelection(Func<McpServerSelectionItemViewModel, bool> isSelected)
    {
        _applyingMcpSelection = true;
        try
        {
            foreach (var server in McpServers)
            {
                server.IsEnabledForSession = isSelected(server);
            }
        }
        finally
        {
            _applyingMcpSelection = false;
        }
    }

    // A tick the operator made themselves (not our own re-apply) makes the checklist sticky, so a later profile switch
    // no longer re-applies the profile's pre-selection over their choice (AC-130 review finding 2).
    private void _OnMcpServerToggled(object? sender, PropertyChangedEventArgs e)
    {
        if (!_applyingMcpSelection && e.PropertyName == nameof(McpServerSelectionItemViewModel.IsEnabledForSession))
        {
            _mcpSelectionTouched = true;
        }

        // The running total (AC-134) follows ticking a server as well as an estimate arriving for one.
        if (e.PropertyName is nameof(McpServerSelectionItemViewModel.IsEnabledForSession)
            or nameof(McpServerSelectionItemViewModel.TokenEstimate)
            or nameof(McpServerSelectionItemViewModel.IsEstimatingTokens))
        {
            OnPropertyChanged(nameof(McpToolTokenSummary));
        }

        // The AC-355 hint names only the *ticked* servers that need a sign-in, so unticking one drops it from the
        // hint (and can clear it entirely) the same way ticking one back on can bring it back.
        if (e.PropertyName is nameof(McpServerSelectionItemViewModel.IsEnabledForSession)
            or nameof(McpServerSelectionItemViewModel.AuthState))
        {
            OnPropertyChanged(nameof(ShowMcpAuthorizationHint));
            OnPropertyChanged(nameof(McpAuthorizationHintText));
        }
    }

    // The AC-134 pre-flight summary line for the ticked MCP servers; shown only once there are servers and an estimator to compute it.
    public bool HasMcpTokenSummary => _tokenEstimator is not null && McpServers.Count > 0;

    // The rolled-up tool-token estimate for the ticked MCP servers (AC-134), labelled as a tools-only estimate.
    public string McpToolTokenSummary => McpTokenEstimation.SummaryLabel(McpServers);

    // Re-enumerates every MCP server's tools and refreshes the estimate (AC-134) — for when a server's toolset has changed since it was last counted.
    [RelayCommand]
    private Task RefreshMcpTokens() => _EstimateMcpTokensAsync(refresh: true);

    private async Task _EstimateMcpTokensAsync(bool refresh)
    {
        if (_tokenEstimator is null || McpServers.Count == 0)
        {
            return;
        }

        _tokenEstimateCts?.Cancel();
        _tokenEstimateCts?.Dispose();
        _tokenEstimateCts = new CancellationTokenSource();
        var token = _tokenEstimateCts.Token;

        try
        {
            await McpTokenEstimation.EstimateAllAsync([.. McpServers], _tokenEstimator, refresh, token);
        }
        catch (OperationCanceledException)
        {
            // A newer estimate run (or the dialog closing) superseded this one — nothing to surface.
        }
    }

    // Rebuilds the quick-pick from the loaded history — favorites first (deduped against recents), then the
    // remaining recents — and refreshes the derived flags.
    private void _RefreshRememberedPaths()
    {
        RememberedPaths.Clear();

        // Always first, and independent of any history, so cloning a not-yet-local repository (AC-90) is one pick away
        // even on a fresh machine with no remembered folders.
        RememberedPaths.Add(new RememberedPathOption(CloneFromUrlLabel, IsFavorite: false, IsCloneAction: true));

        foreach (var path in _history.Favorites)
        {
            RememberedPaths.Add(new RememberedPathOption(path, IsFavorite: true));
        }

        // Recents that aren't already pinned above. A ruler (AC-131) separates the two groups, but only when both
        // exist — no leading/trailing/lonely divider when one side is empty.
        var recents = _history.Recent.Where(path => !_history.IsFavorite(path)).ToList();
        if (_history.Favorites.Count > 0 && recents.Count > 0)
        {
            RememberedPaths.Add(new RememberedPathOption(string.Empty, IsFavorite: false, IsSeparator: true));
        }

        foreach (var path in recents)
        {
            RememberedPaths.Add(new RememberedPathOption(path, IsFavorite: false));
        }

        OnPropertyChanged(nameof(HasRememberedPaths));
        OnPropertyChanged(nameof(IsWorkingDirectoryFavorite));
        OnPropertyChanged(nameof(FavoriteToggleGlyph));
    }

    // Anything but our own fill from the project is the operator naming the session; from there it is theirs, and a
    // project switch no longer overwrites it — nor does the launch treat it as composed.
    partial void OnSessionNameChanged(string value)
    {
        if (!_applyingProjectSessionName)
        {
            _sessionNameTouched = true;
        }
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
        // Any change that is not us applying a profile's default folder is the operator's own — from here on their
        // folder is sticky and a profile switch won't overwrite it (AC-130 review finding 1).
        if (!_applyingProfileWorkingDirectory)
        {
            _workingDirectoryTouched = true;
        }

        OnPropertyChanged(nameof(IsWorkingDirectoryFavorite));
        OnPropertyChanged(nameof(FavoriteToggleGlyph));
        OnPropertyChanged(nameof(CanFavoriteWorkingDirectory));
        _ = _DetectWorkingDirectoryRepoAsync(value);
    }

    partial void OnSelectedRememberedPathChanged(RememberedPathOption? value)
    {
        if (value is null)
        {
            return;
        }

        // The ruler between favorites and recents (AC-131) is not a folder — its container is disabled so this is
        // rarely reached, but guard anyway: clear the selection and do nothing rather than blank the folder field.
        if (value.IsSeparator)
        {
            Dispatcher.UIThread.Post(() => SelectedRememberedPath = null);
            return;
        }

        // The "Clone from a Git URL…" entry is not a folder: hand it to the host's clone flow rather than dropping its
        // label into the folder field. The selection still clears (below) so it behaves as a one-shot action.
        if (value.IsCloneAction)
        {
            Dispatcher.UIThread.Post(() => SelectedRememberedPath = null);
            CloneFromUrlRequested?.Invoke();
            return;
        }

        // Act as a picker, not a persistent selection: fill the field, then clear the selection so the same entry can
        // be re-picked and the dropdown shows its placeholder again. Cleared on the next dispatcher tick, not inline:
        // setting SelectedItem to null synchronously inside the ComboBox's own selection-changed pass is dropped by
        // the control, so the pick would stay shown — and after a Browse to another folder it would then be unclear
        // which path the session uses. Deferring lets the control settle the selection, then empties it.
        WorkingDirectory = value.Path;
        Dispatcher.UIThread.Post(() => SelectedRememberedPath = null);
    }

    // Clears the working directory back to the global default for this session.
    [RelayCommand]
    private void ClearWorkingDirectory() => WorkingDirectory = string.Empty;

    // Forgets a remembered folder from the quick-pick (AC-131): removes it from the recent and favorite lists,
    // persists, and rebuilds the list. The ✕ button that invokes this handles its own pointer press, so the row is
    // never selected (and the folder field never filled) by the same click. Ignores the clone action / separator.
    [RelayCommand]
    private async Task RemoveRememberedPathAsync(RememberedPathOption? option)
    {
        if (option is not { IsRemovable: true } || _workingPathStore is null)
        {
            return;
        }

        _history = await _workingPathStore.RemoveAsync(option.Path);
        _RefreshRememberedPaths();
    }

    // Pins or unpins the current working directory as a favorite, persisting immediately.
    [RelayCommand]
    private async Task ToggleWorkingDirectoryFavoriteAsync()
    {
        var path = WorkingDirectory.Trim();
        if (string.IsNullOrEmpty(path) || _workingPathStore is null)
        {
            return;
        }

        _history = await _workingPathStore.SetFavoriteAsync(path, !_history.IsFavorite(path));
        _RefreshRememberedPaths();
    }

    partial void OnSelectedProfileChanged(SessionProfile? value)
    {
        // The generic login checker gates whichever provider declares a login; a profile whose provider has no
        // gate (or a profile-less/local session) reports logged in, so gating never falsely blocks it.
        IsSelectedProfileLoggedIn = value is not null && (_loginChecker?.IsLoggedIn(value) ?? true);
        // AC-713: a running flow belongs to the profile that started it — switching away must not leave a stale
        // one showing (or submittable) against whatever profile is selected now.
        LoginFlow = null;
        OnPropertyChanged(nameof(HasLoginConcept));
        LoginCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedProfileConfigDir));
        OnPropertyChanged(nameof(IsClaudeProfile));
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(ShowResumeOptions));
        OnPropertyChanged(nameof(SelectedProviderLabel));
        OnPropertyChanged(nameof(ShowWorktreeIsolation));

        _RefreshPluginTtyOptions(value);
        _RefreshSdkLaunchOptions(value);
        OnPropertyChanged(nameof(HasTtyProvider));

        // Seed Kind from the newly selected profile (AC-139): its saved default (falling back to TTY), unless it
        // has no TTY provider to run at all (a local HTTP provider has none; neither does a plugin provider that
        // registered no IPluginTtyProvider) — then SDK wins regardless of the saved default, and the view hides
        // the kind selector, rather than leaving Kind on whatever it was and silently launching the wrong CLI once
        // Start is pressed. Suppress the refresh this kind change would otherwise fire, so the one at the end of
        // this method is the single refresh for the settled kind (no double spawn).
        _suppressDynamicOptionsRefresh = true;
        try
        {
            SelectedKind = SessionKindDefaults.ResolveDefaultKind(value, _ttyProviderResolver);
        }
        finally
        {
            _suppressDynamicOptionsRefresh = false;
        }

        OnPropertyChanged(nameof(ShowSessionOptions));
        OnPropertyChanged(nameof(ShowSdkStartHint));
        OnPropertyChanged(nameof(ShowTtyStartHint));

        // The typed permission/model/effort back the retired Claude-CLI block (hidden now Claude is a plugin); a plugin
        // profile's real defaults pre-fill the generic option rows from OptionDefaults instead. Seed the typed fields
        // with app defaults rather than the profile's legacy typed values, which are migration-only.
        SelectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;
        SelectedClaudeModel = SessionOptionCatalog.DefaultModel.Value;
        SelectedEffort = SessionOptionCatalog.DefaultEffort;

        // Inherit the profile's default view (AC-138): the override picker opens on the profile's chosen reading
        // level, and the operator can still change it for this one session before Start.
        SelectedReadingLevel = SessionOptionCatalog.ResolveReadingLevel(value?.Defaults?.DefaultReadingLevel);

        // Pre-fill the folder and the MCP checklist from the profile's saved defaults (AC-130), so a per-project
        // profile lands in its folder with its servers ticked. Sticky: this keeps applying the newly-selected
        // profile's defaults until the operator sets the folder / edits the checklist themselves, after which their
        // choice stands and a profile re-select (or the Manage-profiles reload) no longer overwrites it.
        // Through the resolver rather than straight off the profile, so a chosen project keeps overriding here too:
        // reading the profile alone blanked the folder the project had just filled whenever the profile named none.
        if (!_workingDirectoryTouched)
        {
            _applyingProfileWorkingDirectory = true;
            try
            {
                WorkingDirectory = SessionStartDefaults.Resolve(SelectedProject, value, memorySources: _memorySources).WorkingDirectory ?? string.Empty;
            }
            finally
            {
                _applyingProfileWorkingDirectory = false;
            }
        }

        if (!_mcpSelectionTouched)
        {
            _ApplyProfileMcpSelection();
        }

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();

        // Kind is settled above; upgrade whichever kind is active with its provider's live options.
        _RefreshDynamicLaunchOptions(value);
    }

    // Rebuilds `PluginTtyOptions` from the selected profile's own plugin TTY provider (if any) —
    // the start defaults it declared via `TtyProviderRegistration.Options`, rendered generically here
    // since the host does not (and must not) know what any of them mean, only their key/label/choices.
    private void _RefreshPluginTtyOptions(SessionProfile? profile)
    {
        PluginTtyOptions.Clear();

        if (_ttyProviderRegistry is not null
            && profile?.ProviderConfig is PluginProviderConfig plugin
            && _ttyProviderRegistry.Resolve(plugin.ProviderId) is { } registration)
        {
            var storedDefaults = profile.Defaults?.OptionDefaults;
            foreach (var option in registration.Options)
            {
                var value = storedDefaults?.GetValueOrDefault(option.Key) ?? option.DefaultValue;
                PluginTtyOptions.Add(new PluginTtyOptionSelectionViewModel(option.Key, option.Label, option.Choices, value, option.ChoiceLabels));
            }
        }

        OnPropertyChanged(nameof(HasPluginTtyOptions));
        OnPropertyChanged(nameof(ShowPluginTtyOptions));
    }

    // Rebuilds `SdkLaunchOptions` from the selected profile's SDK session provider (if any) — the
    // start defaults it declared via `SessionProviderRegistration.Options`, rendered generically here the
    // same way as the TTY route, since the host must not know what any of them mean. Static only; the live
    // upgrade (Codex's model/list) runs from `_RefreshDynamicLaunchOptions` for the active kind.
    private void _RefreshSdkLaunchOptions(SessionProfile? profile)
    {
        SdkLaunchOptions.Clear();

        if (_sessionProviderRegistry is not null
            && profile?.ProviderConfig is PluginProviderConfig plugin
            && _sessionProviderRegistry.Resolve(plugin.ProviderId) is { } registration)
        {
            var storedDefaults = profile.Defaults?.OptionDefaults;
            foreach (var option in registration.Options)
            {
                var value = storedDefaults?.GetValueOrDefault(option.Key) ?? option.DefaultValue;
                SdkLaunchOptions.Add(new PluginTtyOptionSelectionViewModel(option.Key, option.Label, option.Choices, value, option.ChoiceLabels));
            }
        }

        OnPropertyChanged(nameof(HasSdkLaunchOptions));
        OnPropertyChanged(nameof(ShowSdkLaunchOptions));
    }

    // Upgrades the *active* kind's launch options with the provider's live values (Codex's model/list) in
    // the background — only the visible kind, so a Codex profile (which registers both a TTY and an SDK provider)
    // never runs the query twice, and only when that kind's provider offers a resolver. The declared options are
    // already rendered; this replaces their rows when the resolve lands. Runs on a profile change and on a kind
    // switch, since the newly active kind may not have been resolved yet.
    private void _RefreshDynamicLaunchOptions(SessionProfile? profile)
    {
        // A profile or kind switch supersedes any refresh still running.
        _launchOptionsRefreshCts?.Cancel();
        _launchOptionsRefreshCts = null;

        if (profile?.ProviderConfig is not PluginProviderConfig plugin)
        {
            return;
        }

        if (IsTty && _ttyProviderRegistry?.Resolve(plugin.ProviderId) is { ResolveOptionsAsync: { } resolveTty })
        {
            _StartLaunchOptionsRefresh(PluginTtyOptions, plugin.ConfigJson,
                async (json, token) => (await resolveTty(json, token).ConfigureAwait(false)).Select(_ToSpec).ToList());
        }
        else if (IsSdk && _sessionProviderRegistry?.Resolve(plugin.ProviderId) is { ResolveOptionsAsync: { } resolveSdk })
        {
            _StartLaunchOptionsRefresh(SdkLaunchOptions, plugin.ConfigJson,
                async (json, token) => (await resolveSdk(json, token).ConfigureAwait(false)).Select(_ToSpec).ToList());
        }
    }

    private void _StartLaunchOptionsRefresh(
        ObservableCollection<PluginTtyOptionSelectionViewModel> target,
        string configJson,
        Func<string, CancellationToken, Task<IReadOnlyList<LaunchOptionSpec>>> resolveSpecs)
    {
        var cts = new CancellationTokenSource(_LaunchOptionsRefreshTimeout);
        _launchOptionsRefreshCts = cts;
        LaunchOptionsRefresh = _RunLaunchOptionsRefreshAsync(target, configJson, resolveSpecs, cts);
    }

    private async Task _RunLaunchOptionsRefreshAsync(
        ObservableCollection<PluginTtyOptionSelectionViewModel> target,
        string configJson,
        Func<string, CancellationToken, Task<IReadOnlyList<LaunchOptionSpec>>> resolveSpecs,
        CancellationTokenSource cts)
    {
        try
        {
            // Task.Run so the synchronous spawn prefix (Process.Start) never runs on the UI thread; ConfigureAwait
            // keeps the continuation on it, since applying the result mutates a bound collection.
            var resolved = await Task.Run(() => resolveSpecs(configJson, cts.Token), cts.Token).ConfigureAwait(true);

            // Ignore a result the operator has already moved past (a newer refresh replaced this cts).
            if (ReferenceEquals(_launchOptionsRefreshCts, cts))
            {
                _ApplyResolvedLaunchOptions(target, resolved);
            }
        }
        catch (Exception)
        {
            // codex missing, logged out, timed out, or refused — keep the declared options (Model as free text).
        }
        finally
        {
            if (ReferenceEquals(_launchOptionsRefreshCts, cts))
            {
                _launchOptionsRefreshCts = null;
            }

            cts.Dispose();
        }
    }

    // Swaps the declared option rows in `target` for the provider's refreshed ones, carrying
    // over any value the operator already picked (so a Sandbox choice or a typed model survives the model/list
    // arriving) and otherwise taking the refreshed default. `PluginTtyOptionSelectionViewModel.Choices`
    // is fixed per row, so turning a free-text field into a dropdown means replacing the row, not mutating it.
    private void _ApplyResolvedLaunchOptions(ObservableCollection<PluginTtyOptionSelectionViewModel> target, IReadOnlyList<LaunchOptionSpec> resolved)
    {
        var pickedByKey = target.ToDictionary(option => option.Key, option => option.Value);
        target.Clear();
        foreach (var spec in resolved)
        {
            var picked = pickedByKey.GetValueOrDefault(spec.Key);
            var value = string.IsNullOrWhiteSpace(picked) ? spec.DefaultValue : picked;
            target.Add(new PluginTtyOptionSelectionViewModel(spec.Key, spec.Label, spec.Choices, value, spec.ChoiceLabels));
        }

        // The target is one of the two option collections; raise both pairs rather than thread which through.
        OnPropertyChanged(nameof(HasSdkLaunchOptions));
        OnPropertyChanged(nameof(ShowSdkLaunchOptions));
        OnPropertyChanged(nameof(HasPluginTtyOptions));
        OnPropertyChanged(nameof(ShowPluginTtyOptions));
    }

    private static LaunchOptionSpec _ToSpec(PluginSessionLaunchOption option) => new(option.Key, option.Label, option.Choices, option.DefaultValue, option.ChoiceLabels);

    private static LaunchOptionSpec _ToSpec(PluginTtyLaunchOption option) => new(option.Key, option.Label, option.Choices, option.DefaultValue, option.ChoiceLabels);

    // The provider-neutral shape both a TTY and an SDK launch option project to, so one refresh path serves both.
    private readonly record struct LaunchOptionSpec(string Key, string Label, IReadOnlyList<string> Choices, string? DefaultValue, IReadOnlyDictionary<string, string>? ChoiceLabels);

    partial void OnIsSelectedProfileLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginStatusLabel));
        OnPropertyChanged(nameof(LoginStatusBrushKey));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedKindChanged(SessionKind value)
    {
        OnPropertyChanged(nameof(IsSdk));
        OnPropertyChanged(nameof(IsTty));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ShowSdkStartHint));
        OnPropertyChanged(nameof(ShowTtyStartHint));
        OnPropertyChanged(nameof(ShowPluginTtyOptions));
        OnPropertyChanged(nameof(ShowSdkLaunchOptions));
        // Kind drives the start gate (TTY needs no login) and the login hint (SDK-only), so both re-evaluate.
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();

        // The newly active kind may not have had its live options fetched yet (they are fetched per active kind).
        // Skipped while a profile switch is settling its kind — that switch fires its own single refresh.
        if (!_suppressDynamicOptionsRefresh)
        {
            _RefreshDynamicLaunchOptions(SelectedProfile);
        }
    }

    [RelayCommand]
    private void SelectSdk() => SelectedKind = SessionKind.Sdk;

    [RelayCommand]
    private void SelectTty() => SelectedKind = SessionKind.Tty;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Confirm()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(SessionName) ? null : SessionName.Trim();

        // An explicit selection whenever a project is in play, empty included: null means "this launch made no
        // selection", which downstream answers with the profile's saved one over the unscoped registry — and for a
        // project whose overlay left nothing to offer that would mount exactly the servers it had switched off.
        // Without a project the old meaning still holds: an empty checklist is a cockpit with no servers, not a
        // narrowing, and the profile's own selection is the better answer there.
        IReadOnlySet<string>? enabledMcpServerNames = HasMcpServers || SelectedProject is not null
            ? McpServers.Where(server => server.IsEnabledForSession).Select(server => server.Name).ToHashSet()
            : null;
        var workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim();

        // Remember a used directory so next time it is a click away. Fire-and-forget: closing the dialog must
        // not wait on the small config write, and a persistence hiccup shouldn't block starting the session.
        if (workingDirectory is not null && _workingPathStore is not null)
        {
            _ = _workingPathStore.RecordRecentAsync(workingDirectory);
        }

        // A plugin TTY provider's own declared options only apply when TTY is actually chosen for it — never
        // alongside Claude's mode/model/effort, which the result always carries regardless of kind/provider.
        IReadOnlyDictionary<string, string>? pluginTtyOptions = ShowPluginTtyOptions
            ? PluginTtyOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                .ToDictionary(option => option.Key, option => option.Value!)
            : null;

        // The SDK provider's own declared options only apply when SDK is chosen for it — the same key/value
        // shape as the TTY options above, in the provider's own vocabulary (sandbox, model).
        IReadOnlyDictionary<string, string>? sdkLaunchOptions = ShowSdkLaunchOptions
            ? SdkLaunchOptions
                .Where(option => !string.IsNullOrWhiteSpace(option.Value))
                .ToDictionary(option => option.Key, option => option.Value!)
            : null;

        // Isolation only when the folder is actually a git repository — the checkbox disables otherwise, and this is
        // the belt-and-suspenders so a stale check can never send a true for a non-repo the backend would ignore.
        var isolateInWorktree = IsolateInWorktree && IsWorkingDirectoryGitRepo;

        // Resolved at Start rather than read off the project directly, so what the session actually launches with is
        // the same precedence every other surface applies (AC-142/AC-163): the profile's identity, the project's
        // instructions appended under it.
        // The probe is I/O, which Resolve deliberately never does itself (see its own remarks) — Start is an actual
        // launch, not a preview, so this is the one call site in this dialog worth paying a filesystem check for.
        var unresolvedReferences = ProjectResourceProbe.FindUnresolved(SelectedProject?.Resources ?? []);

        // Reading a ticked Instructions row's content (AC-486) is the same kind of I/O, and for the same reason
        // never done inside Resolve itself — this is the other of the two launch call sites its own remarks name.
        var instructionContents = ProjectInstructionContentReader.Read(SelectedProject?.Resources ?? []);
        var startDefaults = SessionStartDefaults.Resolve(
            SelectedProject, SelectedProfile,
            memorySources: _memorySources,
            unresolvedReferences: unresolvedReferences,
            instructionContents: instructionContents);

        CloseRequested?.Invoke(new NewSessionResult(
            SelectedKind, SelectedProfile, SelectedPermissionMode, SessionOptionCatalog.ModelForValue(SelectedClaudeModel), SelectedEffort, name,
            enabledMcpServerNames, workingDirectory, _Resume(), pluginTtyOptions, sdkLaunchOptions, isolateInWorktree,
            // A reading level is an SDK-only concept (AC-138); a TTY session carries none, so the override is left null there.
            IsSdk ? SelectedReadingLevel.Value : null,
            SelectedProject?.Id,
            startDefaults.SystemPrompt)
        {
            // A name we filled in from the project is the cockpit's, not the operator's — so it stays open to a ticket
            // link relabelling it later, exactly as the quick-start's own composed name does (#AC-310/#AC-324).
            NameIsComposed = !_sessionNameTouched,
        });
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void ManageProfiles() => ManageProfilesRequested?.Invoke();
}

// One entry in the New-session dialog's working-directory quick-pick: the remembered `Path` and whether
// it is a pinned favorite (shown with a star icon, and listed first). `IsCloneAction` marks the special
// "Clone from a Git URL…" entry (AC-90) rather than a folder — selecting it opens the clone flow instead of filling
// the folder field. `IsSeparator` marks the non-selectable ruler between the favorites and the recents
// (AC-131). A real folder row (neither action nor separator) carries a ✕ to forget it.
public sealed record RememberedPathOption(string Path, bool IsFavorite, bool IsCloneAction = false, bool IsSeparator = false)
{
    // Whether picking this entry does something — a folder or the clone action. The separator is inert, so its container is disabled and it never fills the field.
    public bool IsSelectable => !IsSeparator;

    // Whether this entry can be forgotten via the ✕ — a real remembered folder, not the clone action or the separator (AC-131).
    public bool IsRemovable => !IsCloneAction && !IsSeparator;
}

// One start default a plugin TTY provider declared (`PluginTtyLaunchOption`) — `Key`/
// `Label`/`Choices` straight from the registration, plus the operator's pick so
// far. `Value` starts blank rather than defaulting to the first choice when the provider left
// `DefaultValue` null: "no choice made" and "the first choice" are different things, and only the
// provider's own default counts as the second one (mirrors `TtyViewModel._LaunchOptions`'s same
// rule for a blank knob). A blank `Value` for a provider with `Choices` renders as
// no selection — the CLI's own default then applies, same as leaving Claude's mode/model/effort untouched.
public sealed partial class PluginTtyOptionSelectionViewModel : ObservableObject
{
    public string Key { get; }

    public string Label { get; }

    public IReadOnlyList<string> Choices { get; }

    // The choices as label/value pairs for the combo, so a provider that supplied friendly labels (Claude's "Ask permissions" for `default`) shows them while `Value` still round-trips the raw value.
    public IReadOnlyList<SelectableChoice> ChoiceItems { get; }

    // No declared choices means free text — the New-session dialog renders a text box instead of a combo.
    public bool IsFreeText => Choices.Count == 0;

    [ObservableProperty]
    private string? _value;

    public PluginTtyOptionSelectionViewModel(string key, string label, IReadOnlyList<string> choices, string? defaultValue, IReadOnlyDictionary<string, string>? choiceLabels = null)
    {
        Key = key;
        Label = label;
        Choices = choices;
        ChoiceItems = [.. choices.Select(value => new SelectableChoice(value, choiceLabels?.GetValueOrDefault(value) ?? value))];
        _value = defaultValue;
    }
}
