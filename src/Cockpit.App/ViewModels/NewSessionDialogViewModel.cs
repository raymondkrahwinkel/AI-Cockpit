using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.WorkingPaths;
using Cockpit.Core.Worktrees;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the New-session dialog (#31/#17/#15/#32/#44): pick the session kind (SDK vs TTY), a profile, and
/// its start mode/model/effort. Choosing a profile loads its saved defaults (<see cref="ProfileDefaults"/>),
/// which the operator can still override before starting. Mode here offers all four modes including
/// bypass, since bypass is launch-only and this dialog is the one place it can be chosen. Also lets the
/// operator uncheck individual MCP servers from the shared registry for just this session (#44) via
/// <see cref="McpServers"/>. The view closes via <see cref="CloseRequested"/>, carrying the choices on
/// confirm or null on cancel.
/// </summary>
/// <remarks>
/// <see cref="SelectedKind"/> plus the <see cref="IsSdk"/>/<see cref="IsTty"/> computed pair is the
/// mechanism for showing/hiding kind-specific fields: today only the hint text differs (see
/// <c>NewSessionDialog.axaml</c>), but a later kind-specific field just adds another
/// <c>IsVisible="{Binding IsSdk}"</c>/<c>IsTty</c> binding without touching the rest of the dialog.
/// </remarks>
public partial class NewSessionDialogViewModel : ViewModelBase
{
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly ISessionProfileStore? _profileStore;
    private readonly IMcpServerCatalog? _mcpServerCatalog;
    private readonly IWorkingPathHistoryStore? _workingPathStore;
    private readonly ConversationPickerRegistration? _conversationPicker;
    private readonly ITtySessionProviderResolver? _ttyProviderResolver;
    private readonly IPluginTtyProviderRegistry? _ttyProviderRegistry;
    private readonly IPluginProviderRegistry? _sessionProviderRegistry;
    private readonly IWorktreeManager? _worktreeManager;
    private WorkingPathHistory _history = WorkingPathHistory.Empty;
    private CancellationTokenSource? _launchOptionsRefreshCts;
    private CancellationTokenSource? _repoDetectCts;

    /// <summary>
    /// Set while a profile switch is settling its kind, so the kind change it forces does not itself trigger a
    /// dynamic-options refresh — the profile switch fires exactly one refresh at its end, for whatever kind won.
    /// </summary>
    private bool _suppressDynamicOptionsRefresh;

    /// <summary>How long the background model/list refresh may run before the dialog gives up and keeps the declared options.</summary>
    private static readonly TimeSpan _LaunchOptionsRefreshTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The in-flight background refresh of the active kind's launch options (Codex's model/list), so a test can await it. Completed when none is running.</summary>
    internal Task LaunchOptionsRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the dialog should close: the result carries the confirmed choices, or null on cancel.</summary>
    public event Action<NewSessionResult?>? CloseRequested;

    /// <summary>Raised when the operator wants to manage profiles; the host opens the Manage-profiles dialog and reloads.</summary>
    public event Action? ManageProfilesRequested;

    /// <summary>Which kind of session to create; chosen in the dialog itself (#32), defaulting to TTY (Raymond's preferred default).</summary>
    [ObservableProperty]
    private SessionKind _selectedKind = SessionKind.Tty;

    public bool IsSdk => SelectedKind == SessionKind.Sdk;

    public bool IsTty => SelectedKind == SessionKind.Tty;

    /// <summary>Window title, tracking the chosen kind.</summary>
    public string HeaderText => IsSdk ? "New session" : "New session (TTY)";

    /// <summary>
    /// The mode/model/effort options are Claude-CLI concepts, shown only for a Claude profile — TTY passes
    /// them as launch-only CLI flags, SDK keeps them live-switchable. A local provider has none of these,
    /// so the whole block is hidden for it (#26).
    /// </summary>
    // The legacy typed permission/model/effort block is retired: Claude renders its options through the generic
    // plugin-option rows now, like every provider. Kept false until that block and SessionOptionCatalog are removed.
    public bool ShowSessionOptions => false;

    /// <summary>The SDK "stays live-switchable" hint, shown only for a Claude SDK session (a local session has no such dropdowns).</summary>
    public bool ShowSdkStartHint => IsSdk && IsClaudeProfile;

    /// <summary>The TTY "start defaults only" hint, shown only for a Claude TTY session.</summary>
    public bool ShowTtyStartHint => IsTty && IsClaudeProfile;

    public ObservableCollection<SessionProfile> Profiles { get; } = [];

    /// <summary>
    /// The shared registry's enabled MCP servers (#44), each with its own checkbox so the operator can opt
    /// individual ones out of just this session. Defaults to all checked, matching the pre-#44 behaviour.
    /// </summary>
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    /// <summary>Whether the MCP checklist is shown at all — hidden when the registry has no enabled servers.</summary>
    public bool HasMcpServers => McpServers.Count > 0;

    /// <summary>All four modes — including the launch-only bypass — since this dialog is the one place bypass can be chosen.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    /// <summary>Optional friendly name for the session, shown in the sidebar and above the panel; blank falls back to "&lt;profile&gt; - N".</summary>
    [ObservableProperty]
    private string _sessionName = string.Empty;

    /// <summary>
    /// Whether this session picks up an earlier conversation instead of starting fresh — the answer to "the app
    /// crashed and I want to go on where I was". Only the Claude CLI keeps a history to resume from, so the
    /// controls are hidden for a local provider rather than offered and then ignored.
    /// </summary>
    [ObservableProperty]
    private SessionResumeMode _resumeMode = SessionResumeMode.New;

    /// <summary>The conversation to resume when <see cref="ResumeMode"/> is <see cref="SessionResumeMode.BySessionId"/> — the id shown in a session's header, or copied from the transcript-search plugin.</summary>
    [ObservableProperty]
    private string _resumeSessionId = string.Empty;

    /// <summary>Resuming is a Claude-CLI capability; a local provider keeps no conversation history of its own.</summary>
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

    /// <summary>Whether a plugin offers a way to browse the provider's conversation history — no picker, no search button (and the id can still be typed).</summary>
    public bool HasConversationPicker => _conversationPicker is not null;

    /// <summary>Tooltip for the search button: whatever the picker calls itself, e.g. "Search transcripts".</summary>
    public string ConversationPickerTitle => _conversationPicker?.Title ?? string.Empty;

    /// <summary>Runs the plugin's picker and fills in whatever conversation the operator chose. Cancelling leaves the field as it was.</summary>
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

    /// <summary>The choice as the session layer consumes it; a blank id falls back to a fresh conversation rather than a broken resume.</summary>
    private SessionResume _Resume() => ResumeMode switch
    {
        SessionResumeMode.MostRecent => SessionResume.MostRecent,
        SessionResumeMode.BySessionId when !string.IsNullOrWhiteSpace(ResumeSessionId) => SessionResume.BySessionId(ResumeSessionId.Trim()),
        _ => SessionResume.New,
    };

    /// <summary>
    /// Optional per-session working directory (#: project-folder launch): the directory <c>claude</c> is
    /// started in for this session, overriding the global option. Blank keeps the global default. Pre-fillable
    /// from <see cref="RecentPaths"/>/<see cref="FavoritePaths"/> so a previously-used folder is one click away.
    /// </summary>
    [ObservableProperty]
    private string _workingDirectory = string.Empty;

    /// <summary>Remembered working directories for the quick-pick — favorites first (★), then recents — so a folder used before is one selection away.</summary>
    public ObservableCollection<RememberedPathOption> RememberedPaths { get; } = [];

    /// <summary>Bound to the quick-pick ComboBox; selecting an entry fills <see cref="WorkingDirectory"/> and resets to null so it behaves as a picker, not a persistent selection.</summary>
    [ObservableProperty]
    private RememberedPathOption? _selectedRememberedPath;

    /// <summary>Whether there is anything to offer in the folder quick-pick.</summary>
    public bool HasRememberedPaths => RememberedPaths.Count > 0;

    /// <summary>Whether the currently-typed working directory is pinned — drives the favorite toggle's icon.</summary>
    public bool IsWorkingDirectoryFavorite => _history.IsFavorite(WorkingDirectory);

    /// <summary>Filled star when the current folder is a favorite, outline otherwise — the toggle button's icon.</summary>
    public MaterialIconKind FavoriteToggleGlyph => IsWorkingDirectoryFavorite ? MaterialIconKind.Star : MaterialIconKind.StarOutline;

    /// <summary>Whether the favorite toggle is actionable (there is a path to pin).</summary>
    public bool CanFavoriteWorkingDirectory => !string.IsNullOrWhiteSpace(WorkingDirectory);

    /// <summary>Whether this session runs in its own git worktree on a dedicated branch (AC-85) — a per-session choice made here, next to the folder, not a profile setting. Only actionable when the folder is a git repository; forced off otherwise so it is never a silent no-op.</summary>
    [ObservableProperty]
    private bool _isolateInWorktree;

    /// <summary>Whether the current working directory is a git repository — the reactive gate (§4) for offering worktree isolation.</summary>
    [ObservableProperty]
    private bool _isWorkingDirectoryGitRepo;

    /// <summary>The branch the current working directory is on, when it is a git repository — the base a worktree would branch from, shown in the isolation status line.</summary>
    [ObservableProperty]
    private string? _workingDirectoryBaseBranch;

    /// <summary>Whether the isolation checkbox is actionable (the folder is a git repository).</summary>
    public bool CanIsolateInWorktree => IsWorkingDirectoryGitRepo;

    /// <summary>The status line under the isolation checkbox: a repo shows the base branch it would fork from, a non-repo says why the option is disabled (§4).</summary>
    public string WorktreeStatusText => IsWorkingDirectoryGitRepo
        ? $"✓ Git repo{(string.IsNullOrEmpty(WorkingDirectoryBaseBranch) ? string.Empty : $" · base {WorkingDirectoryBaseBranch}")} → runs on a new cockpit/… branch"
        : "Pick a Git repository to enable isolation";

    /// <summary>
    /// Whether to show the isolation control — for any session that runs in a working directory (SDK or TTY alike;
    /// isolation is resolved once for both kinds). Hidden only for a local HTTP provider (Ollama/LM Studio), which
    /// spawns no process and so has no working tree to isolate. Shown greyed with a hint until the folder is a git
    /// repository (§4), so the option is discoverable rather than appearing only once a repo is picked.
    /// </summary>
    public bool ShowWorktreeIsolation => SelectedProfile is not null && !IsLocalProfile;

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
    }

    [ObservableProperty]
    private SessionProfile? _selectedProfile;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    /// <summary>The Claude model for this session, as free text with suggestions — an alias, or a pinned model/snapshot.</summary>
    [ObservableProperty]
    private string _selectedClaudeModel = SessionOptionCatalog.DefaultModel.Value;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    [ObservableProperty]
    private bool _isSelectedProfileLoggedIn;

    /// <summary>Config directory of the selected profile, shown under the picker so it is clear where its login lives.</summary>
    public string? SelectedProfileConfigDir => SelectedProfile?.Claude?.ConfigDir;

    /// <summary>
    /// Whether the selected profile runs on Claude (login gating, resume, the login hint apply) — true whether it
    /// still carries a legacy <see cref="ClaudeConfig"/> or, after Fase 4, the bundled Claude provider plugin's
    /// config. A profile's <see cref="SessionProfile.Claude"/> is non-null in both cases.
    /// </summary>
    public bool IsClaudeProfile => SelectedProfile?.Claude is not null;

    /// <summary>A local OpenAI-compatible provider (Ollama/LM Studio) — no login, no TUI, no resume.</summary>
    public bool IsLocalProfile => SelectedProfile?.Provider is SessionProvider.Ollama or SessionProvider.LmStudio;

    /// <summary>
    /// Whether the selected profile has a TUI to run at all — the gate for offering the Kind picker and for
    /// what TTY actually launches. Claude always does (its own <c>claude</c> TTY provider); a plugin profile
    /// does only when it registered one via <c>ICockpitHost.AddTtyProvider</c> under the same provider id its
    /// session provider uses (#45 fase B2) — resolved the same way <c>TtyViewModel</c> resolves it at
    /// launch, so the dialog never offers a kind the launch would then refuse. A local HTTP provider (Ollama/
    /// LM Studio) is never a program a terminal can host, so it has none either way.
    /// </summary>
    public bool HasTtyProvider => IsClaudeProfile || (_ttyProviderResolver?.Resolve(SelectedProfile) is not null);

    /// <summary>The declared start defaults for the selected profile's plugin TTY provider (Codex's sandbox policy, say) — empty for Claude/local profiles or a plugin with none declared.</summary>
    public ObservableCollection<PluginTtyOptionSelectionViewModel> PluginTtyOptions { get; } = [];

    public bool HasPluginTtyOptions => PluginTtyOptions.Count > 0;

    /// <summary>Shown when TTY is chosen for a plugin profile (Claude or Codex) that declared its own start defaults.</summary>
    public bool ShowPluginTtyOptions => IsTty && HasPluginTtyOptions;

    /// <summary>The declared per-session start defaults for the selected profile's SDK session provider (Codex's sandbox/model) — empty for Claude/local profiles or a provider with none declared. Reuses the same generic option row as the TTY route.</summary>
    public ObservableCollection<PluginTtyOptionSelectionViewModel> SdkLaunchOptions { get; } = [];

    public bool HasSdkLaunchOptions => SdkLaunchOptions.Count > 0;

    /// <summary>Shown when SDK is chosen for a plugin profile (Claude or Codex) that declared its own start defaults — the SDK mirror of <see cref="ShowPluginTtyOptions"/>.</summary>
    public bool ShowSdkLaunchOptions => IsSdk && HasSdkLaunchOptions;

    /// <summary>Provider label shown next to the picker; empty for Claude, which needs no badge.</summary>
    public string SelectedProviderLabel => IsLocalProfile ? SessionProviderCatalog.Resolve(SelectedProfile!.Provider).Label : string.Empty;

    public string LoginStatusLabel => IsSelectedProfileLoggedIn ? "logged in" : "not logged in";

    /// <summary>Guidance shown (in the body) only when a Claude SDK session isn't logged in — a TTY session logs in via its own TUI, and a local provider has no login.</summary>
    public bool ShowLoginHint => IsClaudeProfile && IsSdk && SelectedProfile is not null && !IsSelectedProfileLoggedIn;

    public string LoginStatusBrushKey => IsSelectedProfileLoggedIn
        ? "CockpitStatusDoneBrush"
        : "CockpitStatusWaitingBrush";

    /// <summary>
    /// A selected profile is startable, except a Claude <em>SDK</em> session, which is gated on login: an
    /// SDK spawn talks to the CLI headlessly and would just fail unauthenticated. A TTY session hosts the
    /// real interactive TUI, which runs its own <c>/login</c>, so it needs no pre-check; a local profile
    /// has no login at all.
    /// </summary>
    /// <summary>Resuming by id with no id typed is not a session anyone asked for, so Start stays disabled until it is filled in.</summary>
    public bool CanStart =>
        SelectedProfile is not null
        && (IsLocalProfile || IsTty || IsSelectedProfileLoggedIn)
        && (ResumeMode != SessionResumeMode.BySessionId || !string.IsNullOrWhiteSpace(ResumeSessionId));

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
        IWorktreeManager? worktreeManager = null)
    {
        _conversationPicker = conversationPickers?.Pickers.FirstOrDefault();
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _mcpServerCatalog = mcpServerCatalog;
        _workingPathStore = workingPathStore;
        _ttyProviderResolver = ttyProviderResolver;
        _ttyProviderRegistry = ttyProviderRegistry;
        _sessionProviderRegistry = sessionProviderRegistry;
        _worktreeManager = worktreeManager;
    }

    /// <summary>
    /// Loads the profiles and selects the first, so the dialog opens ready to confirm. Also loads the enabled MCP
    /// servers (#44) into the checklist, all pre-checked — from the catalog, so a plugin's own servers (AC-11) are
    /// offered and uncheckable here alongside the registry's.
    /// </summary>
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

        if (_mcpServerCatalog is not null)
        {
            var registry = await _mcpServerCatalog.GetServersAsync();
            McpServers.Clear();
            foreach (var server in registry.Where(server => server.Enabled))
            {
                McpServers.Add(new McpServerSelectionItemViewModel(server.Name));
            }

            OnPropertyChanged(nameof(HasMcpServers));
        }

        if (_workingPathStore is not null)
        {
            _history = await _workingPathStore.LoadAsync();
            _RefreshRememberedPaths();
        }
    }

    // Rebuilds the quick-pick from the loaded history — favorites first (deduped against recents), then the
    // remaining recents — and refreshes the derived flags.
    private void _RefreshRememberedPaths()
    {
        RememberedPaths.Clear();
        foreach (var path in _history.Favorites)
        {
            RememberedPaths.Add(new RememberedPathOption(path, IsFavorite: true));
        }

        foreach (var path in _history.Recent.Where(path => !_history.IsFavorite(path)))
        {
            RememberedPaths.Add(new RememberedPathOption(path, IsFavorite: false));
        }

        OnPropertyChanged(nameof(HasRememberedPaths));
        OnPropertyChanged(nameof(IsWorkingDirectoryFavorite));
        OnPropertyChanged(nameof(FavoriteToggleGlyph));
    }

    partial void OnWorkingDirectoryChanged(string value)
    {
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

        // Act as a picker, not a persistent selection: fill the field, then clear the selection so the same
        // entry can be re-picked and the ComboBox shows its placeholder again.
        WorkingDirectory = value.Path;
        SelectedRememberedPath = null;
    }

    /// <summary>Clears the working directory back to the global default for this session.</summary>
    [RelayCommand]
    private void ClearWorkingDirectory() => WorkingDirectory = string.Empty;

    /// <summary>Pins or unpins the current working directory as a favorite, persisting immediately.</summary>
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
        OnPropertyChanged(nameof(SelectedProfileConfigDir));
        OnPropertyChanged(nameof(IsClaudeProfile));
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(ShowResumeOptions));
        OnPropertyChanged(nameof(SelectedProviderLabel));
        OnPropertyChanged(nameof(ShowWorktreeIsolation));

        _RefreshPluginTtyOptions(value);
        _RefreshSdkLaunchOptions(value);
        OnPropertyChanged(nameof(HasTtyProvider));

        // A profile with no TTY provider to run only runs as an SDK session (a local HTTP provider has none;
        // neither does a plugin provider that registered no IPluginTtyProvider) — force SDK and let the view
        // hide the kind selector, rather than leaving Kind on whatever it was and silently launching the
        // wrong CLI once Start is pressed. Suppress the refresh this kind change would otherwise fire, so the
        // one at the end of this method is the single refresh for the settled kind (no double spawn).
        _suppressDynamicOptionsRefresh = true;
        try
        {
            if (!HasTtyProvider)
            {
                SelectedKind = SessionKind.Sdk;
            }
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

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();

        // Kind is settled above; upgrade whichever kind is active with its provider's live options.
        _RefreshDynamicLaunchOptions(value);
    }

    /// <summary>
    /// Rebuilds <see cref="PluginTtyOptions"/> from the selected profile's own plugin TTY provider (if any) —
    /// the start defaults it declared via <c>TtyProviderRegistration.Options</c>, rendered generically here
    /// since the host does not (and must not) know what any of them mean, only their key/label/choices.
    /// </summary>
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

    /// <summary>
    /// Rebuilds <see cref="SdkLaunchOptions"/> from the selected profile's SDK session provider (if any) — the
    /// start defaults it declared via <c>SessionProviderRegistration.Options</c>, rendered generically here the
    /// same way as the TTY route, since the host must not know what any of them mean. Static only; the live
    /// upgrade (Codex's model/list) runs from <see cref="_RefreshDynamicLaunchOptions"/> for the active kind.
    /// </summary>
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

    /// <summary>
    /// Upgrades the <em>active</em> kind's launch options with the provider's live values (Codex's model/list) in
    /// the background — only the visible kind, so a Codex profile (which registers both a TTY and an SDK provider)
    /// never runs the query twice, and only when that kind's provider offers a resolver. The declared options are
    /// already rendered; this replaces their rows when the resolve lands. Runs on a profile change and on a kind
    /// switch, since the newly active kind may not have been resolved yet.
    /// </summary>
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

    /// <summary>
    /// Swaps the declared option rows in <paramref name="target"/> for the provider's refreshed ones, carrying
    /// over any value the operator already picked (so a Sandbox choice or a typed model survives the model/list
    /// arriving) and otherwise taking the refreshed default. <see cref="PluginTtyOptionSelectionViewModel.Choices"/>
    /// is fixed per row, so turning a free-text field into a dropdown means replacing the row, not mutating it.
    /// </summary>
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

    /// <summary>The provider-neutral shape both a TTY and an SDK launch option project to, so one refresh path serves both.</summary>
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
        IReadOnlySet<string>? enabledMcpServerNames = HasMcpServers
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

        CloseRequested?.Invoke(new NewSessionResult(
            SelectedKind, SelectedProfile, SelectedPermissionMode, SessionOptionCatalog.ModelForValue(SelectedClaudeModel), SelectedEffort, name,
            enabledMcpServerNames, workingDirectory, _Resume(), pluginTtyOptions, sdkLaunchOptions, isolateInWorktree));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void ManageProfiles() => ManageProfilesRequested?.Invoke();
}

/// <summary>One entry in the New-session dialog's working-directory quick-pick: the remembered <see cref="Path"/> and whether it is a pinned favorite (shown with a star icon, and listed first).</summary>
public sealed record RememberedPathOption(string Path, bool IsFavorite);

/// <summary>
/// One start default a plugin TTY provider declared (<c>PluginTtyLaunchOption</c>) — <see cref="Key"/>/
/// <see cref="Label"/>/<see cref="Choices"/> straight from the registration, plus the operator's pick so
/// far. <see cref="Value"/> starts blank rather than defaulting to the first choice when the provider left
/// <c>DefaultValue</c> null: "no choice made" and "the first choice" are different things, and only the
/// provider's own default counts as the second one (mirrors <c>TtyViewModel._LaunchOptions</c>'s same
/// rule for a blank knob). A blank <see cref="Value"/> for a provider with <see cref="Choices"/> renders as
/// no selection — the CLI's own default then applies, same as leaving Claude's mode/model/effort untouched.
/// </summary>
public sealed partial class PluginTtyOptionSelectionViewModel : ObservableObject
{
    public string Key { get; }

    public string Label { get; }

    public IReadOnlyList<string> Choices { get; }

    /// <summary>The choices as label/value pairs for the combo, so a provider that supplied friendly labels (Claude's "Ask permissions" for <c>default</c>) shows them while <see cref="Value"/> still round-trips the raw value.</summary>
    public IReadOnlyList<SelectableChoice> ChoiceItems { get; }

    /// <summary>No declared choices means free text — the New-session dialog renders a text box instead of a combo.</summary>
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
