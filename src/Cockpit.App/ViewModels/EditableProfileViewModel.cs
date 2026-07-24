using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A mutable, editable view over an immutable <see cref="SessionProfile"/> for the Manage-profiles
/// dialog: the record's fields as editable properties plus its <see cref="ProfileDefaults"/> as three
/// selected options, and the provider (#26). The provider can only be chosen while adding a profile
/// (<see cref="CanChooseProvider"/>) — it is fixed afterwards so credentials/config never go inconsistent.
/// <see cref="ToProfile"/> turns the edits back into a profile on save; empty executable/purpose/api-key
/// collapse to <see langword="null"/> so an unset field stays unset.
/// </summary>
public partial class EditableProfileViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _configDir;

    [ObservableProperty]
    private string _executablePath;

    [ObservableProperty]
    private string _purpose;

    /// <summary>
    /// The working directory a New session under this profile pre-fills (AC-130) — a per-project profile lands in
    /// its project folder without picking one each time. Empty means no default (the folder field opens empty).
    /// </summary>
    [ObservableProperty]
    private string _defaultWorkingDirectory;

    /// <summary>
    /// Standing instructions every session under this profile starts with (AC-142) — who it is and where its
    /// knowledge lives ("you are Olaf; look yourself up in the Depot MCP"). Appended to the provider's own system
    /// prompt, never replacing it. Empty means none.
    /// <para>
    /// Deliberately not called <c>SystemPrompt</c>: that name is already taken in this editor by the local-LLM
    /// provider config's own field (Ollama/LM Studio), which reaches only those two providers. This one is the
    /// profile's, and rides the append-system-prompt option every provider honours.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private string _profileSystemPrompt;

    /// <summary>
    /// Whether this profile pre-selects a specific set of MCP servers (AC-130). Off — the default — means no
    /// restriction: a New session ticks every enabled server, as before. On reveals <see cref="McpServers"/> and
    /// persists exactly the ticked ones, so a project profile need not re-toggle them each time.
    /// </summary>
    [ObservableProperty]
    private bool _restrictMcpServers;

    /// <summary>
    /// A ceiling on the session CLI's memory, in MB — 0 means none, which is what it is unless someone types a
    /// number. The Claude CLI is Node, and a long conversation grows its heap past half a gigabyte; this makes it
    /// collect harder instead. It is also the one setting here that can kill a session mid-turn, which is why it is
    /// off by default and says so in the dialog.
    /// </summary>
    [ObservableProperty]
    private int _memoryLimitMb;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode;

    /// <summary>The Claude model default, as free text with suggestions — an alias, or a pinned model/snapshot.</summary>
    [ObservableProperty]
    private string _claudeModel = SessionOptionCatalog.DefaultModel.Value;

    [ObservableProperty]
    private EffortOption _selectedEffort;

    /// <summary>The three SDK reading levels (AC-138) offered by the "Default view" picker; provider-neutral, since any profile can launch an SDK session.</summary>
    public IReadOnlyList<ReadingLevelOption> ReadingLevels => SessionOptionCatalog.ReadingLevels;

    /// <summary>The reading level a new SDK session under this profile opens with (AC-138) — the profile's "Default view".</summary>
    [ObservableProperty]
    private ReadingLevelOption _selectedReadingLevel = SessionOptionCatalog.DefaultReadingLevel;

    /// <summary>Whether a session under this profile starts with "allow all tools" already on (#26) — only meaningful for a local provider, which gates tool calls per-call rather than through Claude's permission modes.</summary>
    [ObservableProperty]
    private bool _autoApproveTools;

    /// <summary>Whether another session may hand work to this profile (#67). Off by default: delegation spawns a process under this profile's login, so it is opted into.</summary>
    [ObservableProperty]
    private bool _allowedAsTarget;

    /// <summary>What this profile is good for, told to a calling agent so it picks the right one — e.g. "cheap bulk refactors and summarising, no web access".</summary>
    [ObservableProperty]
    private string _delegationPurpose;

    /// <summary>The kinds of work this profile accepts, comma-separated (e.g. "summarize, refactor"); empty accepts anything.</summary>
    [ObservableProperty]
    private string _allowedTaskTypes;

    /// <summary>
    /// The directories a delegated task may run in, one per line. Empty means a caller may not choose one at all —
    /// which is what made every delegation with a working directory fail: the policy existed, but nothing in this
    /// dialog could grant it, so "allowed nowhere" was the only setting a profile could have.
    /// </summary>
    [ObservableProperty]
    private string _allowedWorkingDirs;

    /// <summary>How many delegated tasks may run on this profile at once — the guard on its provider's usage pot (and, for a local model, its GPU).</summary>
    [ObservableProperty]
    private int _maxConcurrentTasks;

    /// <summary>Whether a task running on this profile may itself delegate. Off by default, or a sub-agent could start a chain of agents.</summary>
    [ObservableProperty]
    private bool _mayDelegateFurther;

    /// <summary>How long a delegated task may run here before the cockpit stops it — nobody is watching a delegated session, so a model that loops would otherwise hold the slot forever. 0 = no limit.</summary>
    [ObservableProperty]
    private int _delegationTimeoutMinutes;

    /// <summary>The most permissive class of tool a delegated session on this profile may run unattended (AC-79): plan/default = read-only only, acceptEdits = also non-destructive writes, bypassPermissions = everything. Ignored when "Auto-Approve tool calls" is on (that allows everything).</summary>
    [ObservableProperty]
    private string _permissionCeiling;

    /// <summary>Tool names a delegated session may run unattended regardless of class/ceiling, one per line (AC-79) — the trust anchor for a tool whose MCP server gives no reliable read-only hint.</summary>
    [ObservableProperty]
    private string _allowedTools;

    /// <summary>The permission-ceiling values offered in the delegation dropdown (AC-79), least- to most-permissive.</summary>
    public IReadOnlyList<string> PermissionCeilingChoices { get; } = ["plan", "default", "acceptEdits", "bypassPermissions"];

    [ObservableProperty]
    private SessionProviderOption _selectedProvider;

    /// <summary>Only a freshly added profile may pick its provider; an existing one is fixed (#26), so the dropdown is disabled.</summary>
    [ObservableProperty]
    private bool _canChooseProvider;

    /// <summary>Base URL of the local provider's OpenAI-compatible server (Ollama/LM Studio).</summary>
    [ObservableProperty]
    private string _baseUrl;

    /// <summary>Model id for the local provider (as reported by <c>/v1/models</c>).</summary>
    [ObservableProperty]
    private string _model;

    /// <summary>Optional API key for LM Studio behind a key-protected proxy; unused for Ollama.</summary>
    [ObservableProperty]
    private string _apiKey;

    /// <summary>Optional base system prompt sent as the first message of every conversation for a local provider.</summary>
    [ObservableProperty]
    private string _systemPrompt;

    /// <summary>Login status of this profile's config directory, evaluated once when the dialog loads.</summary>
    [ObservableProperty]
    private bool _isLoggedIn;

    /// <summary>
    /// The plugin-provided "add/edit profile" config panel (#45), built from the registered provider's
    /// <c>CreateConfigView</c> when <see cref="SelectedProvider"/> is a plugin provider; <see langword="null"/>
    /// for a built-in provider, or when no <see cref="IPluginProviderRegistry"/> was supplied (e.g. design-time,
    /// or a test that does not care about plugin providers).
    /// </summary>
    [ObservableProperty]
    private IPluginProviderConfigView? _pluginConfigView;

    private readonly IPluginProviderRegistry? _pluginProviderRegistry;

    /// <summary>
    /// The profile's original <see cref="PluginProviderConfig"/> when it was loaded for a provider id that
    /// did not resolve in <see cref="_pluginProviderRegistry"/> (the plugin is removed/disabled/failed to
    /// load — a normal, lasting state, not a transient error). Carried through <see cref="ToProfile"/>
    /// unchanged so an orphaned profile never loses its <c>ProviderId</c>/<c>ConfigJson</c> (and therefore
    /// any API key inside it) just because nothing could build a <see cref="PluginConfigView"/> for it.
    /// </summary>
    private readonly PluginProviderConfig? _orphanedPluginConfig;

    /// <summary>
    /// Whether this row is a plugin-provider profile whose provider plugin is not currently resolvable
    /// (removed, disabled, or failed to load) — the editor shows a "provider plugin not installed" state
    /// instead of an empty settings region, and <see cref="ToProfile"/> preserves the original config as-is.
    /// </summary>
    public bool IsPluginProviderMissing => IsPluginProvider && PluginConfigView is null;

    public IReadOnlyList<SessionProviderOption> Providers { get; }

    /// <summary>Models the local server reported on the last refresh, offered as suggestions in the model picker.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>
    /// Per-profile default editors for the selected plugin provider's own declared launch options (Claude's
    /// permission mode/model/effort, Codex's sandbox) — rendered generically from the plugin's declaration, so the
    /// host imposes no provider vocabulary. Empty for a built-in provider or a plugin that declares none.
    /// </summary>
    public ObservableCollection<PluginTtyOptionSelectionViewModel> PluginOptionDefaults { get; } = [];

    /// <summary>Whether the selected plugin provider declares any start-option defaults to edit.</summary>
    public bool HasPluginOptionDefaults => PluginOptionDefaults.Count > 0;

    /// <summary>
    /// The profile's spawn environment variables (AC-22), one editable row each. Only shown when the provider
    /// supports injection (<see cref="SupportsEnvVars"/>).
    /// </summary>
    public ObservableCollection<ProfileEnvironmentVariableViewModel> EnvironmentVariables { get; } = [];

    /// <summary>
    /// One checkbox row per available MCP server (registry + plugin-provided) for the profile's pre-selection
    /// (AC-130). Ticked according to the profile's saved selection, or all-ticked when it has none. Only shown when
    /// <see cref="RestrictMcpServers"/> is on; hidden entirely when there are no servers to choose from.
    /// </summary>
    public ObservableCollection<McpServerSelectionItemViewModel> McpServers { get; } = [];

    /// <summary>Whether there are any MCP servers to pre-select — the gate is hidden entirely when the catalog is empty.</summary>
    public bool HasMcpServers => McpServers.Count > 0;

    /// <summary>Saved pre-selected servers the catalog did not offer at load (disabled/absent), so the checklist can't represent them; preserved verbatim by <see cref="ToProfile"/> so a save never silently drops them.</summary>
    private readonly IReadOnlyList<string> _carriedOverMcpServerNames;

    private readonly IMcpToolTokenEstimator? _tokenEstimator;
    private CancellationTokenSource? _tokenEstimateCts;

    /// <summary>The AC-134 pre-flight summary line for the ticked MCP servers; shown only once the pre-selection is revealed and an estimator is available.</summary>
    public bool HasMcpTokenSummary => _tokenEstimator is not null && RestrictMcpServers && McpServers.Count > 0;

    /// <summary>The rolled-up tool-token estimate for the ticked MCP servers (AC-134), labelled as a tools-only estimate.</summary>
    public string McpToolTokenSummary => McpTokenEstimation.SummaryLabel(McpServers);

    /// <summary>Re-enumerates every offered MCP server's tools and refreshes the estimate (AC-134).</summary>
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

        try
        {
            await McpTokenEstimation.EstimateAllAsync([.. McpServers], _tokenEstimator, refresh, _tokenEstimateCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer run or the editor closing.
        }
    }

    // Revealing the pre-selection (AC-130) is when the token estimate becomes worth computing (AC-134): count then,
    // not while the section is hidden. The estimator caches per server, so re-revealing — or another profile using
    // the same servers — does not re-spawn them.
    partial void OnRestrictMcpServersChanged(bool value)
    {
        OnPropertyChanged(nameof(HasMcpTokenSummary));
        if (value)
        {
            _ = _EstimateMcpTokensAsync(refresh: false);
        }
    }

    private void _OnMcpServerRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpServerSelectionItemViewModel.IsEnabledForSession)
            or nameof(McpServerSelectionItemViewModel.TokenEstimate)
            or nameof(McpServerSelectionItemViewModel.IsEstimatingTokens))
        {
            OnPropertyChanged(nameof(McpToolTokenSummary));
        }
    }

    /// <summary>
    /// Whether the selected provider's sessions honour a profile's environment variables at spawn (AC-22) — the
    /// plugin provider's declared capability, the single gate (Claude and Codex are plugins; the retired
    /// Claude-CLI enum resolves to the Ollama fallback and never reaches here). False for the HTTP providers
    /// (Ollama/LM Studio), which spawn nothing to inject into, so the editor never shows as a dead control.
    /// </summary>
    public bool SupportsEnvVars =>
        SelectedProvider.Value == SessionProvider.Plugin
        && SelectedProvider.PluginProviderId is { } providerId
        && _pluginProviderRegistry?.Resolve(providerId)?.Capabilities.SupportsEnvVars == true;

    /// <summary>The alias suggestions for the editable Claude model field (see <see cref="SessionOptionCatalog.ClaudeModelSuggestions"/>).</summary>
    public IReadOnlyList<string> ClaudeModelSuggestions => SessionOptionCatalog.ClaudeModelSuggestions;

    public bool IsClaudeProvider => SelectedProvider.Value == SessionProvider.ClaudeCli;

    /// <summary>The local OpenAI-compatible providers (Ollama/LM Studio) — a plugin provider (#45) is neither this nor <see cref="IsClaudeProvider"/>, so it gets its own <see cref="IsPluginProvider"/>.</summary>
    public bool IsLocalProvider => SelectedProvider.Value is SessionProvider.Ollama or SessionProvider.LmStudio;

    public bool IsLmStudioProvider => SelectedProvider.Value == SessionProvider.LmStudio;

    /// <summary>Whether the selected provider is registered by a plugin (#45) — swaps the fixed local-provider fields for <see cref="PluginConfigView"/>'s content.</summary>
    public bool IsPluginProvider => SelectedProvider.Value == SessionProvider.Plugin;

    /// <summary>
    /// Label shown in the profile list, with the provider (and local model) appended (#26). A plugin
    /// provider (#45) uses <see cref="SelectedProvider"/>'s own display name directly — <see cref="ProfileDisplay"/>
    /// has no registry access to look up a specific plugin's label from the bare enum value.
    /// </summary>
    public string DisplayLabel => IsPluginProvider
        ? $"{Label} ({SelectedProvider.Label})"
        : ProfileDisplay.Format(Label, SelectedProvider.Value, Model);

    /// <summary>Placeholder for the base-URL field, defaulting to the selected local provider's usual localhost address.</summary>
    public string BaseUrlPlaceholder => SessionProviderCatalog.DefaultBaseUrl(SelectedProvider.Value);

    public string LoginStatusLabel => IsLoggedIn ? "logged in" : "not logged in";

    public string LoginStatusBrushKey => IsLoggedIn ? "CockpitStatusDoneBrush" : "CockpitStatusWaitingBrush";

    /// <summary>
    /// Whether this row has the fields its provider needs to launch — a label always, plus a config
    /// directory (Claude), a base URL and model (local), or a plugin config view that validates (#45).
    /// </summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Label)
        && _AreEnvironmentVariablesValid()
        && SelectedProvider.Value switch
        {
            SessionProvider.ClaudeCli => !string.IsNullOrWhiteSpace(ConfigDir),
            SessionProvider.Plugin => PluginConfigView is not null && PluginConfigView.TryGetConfigJson(out _),
            _ => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model),
        };

    // Every row a settable POSIX name, no key twice — a duplicate would silently overwrite its sibling at spawn.
    // Case-insensitive, because the spawn composition is (TtyEnvironment, the Claude driver's environment): two
    // case-variant rows are one variable there, so they are the duplicate this gate exists to catch.
    private bool _AreEnvironmentVariablesValid() =>
        EnvironmentVariables.All(row => row.IsKeyValid)
        && EnvironmentVariables.Select(row => row.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() == EnvironmentVariables.Count;

    [RelayCommand]
    private void AddEnvironmentVariable() => EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel());

    [RelayCommand]
    private void RemoveEnvironmentVariable(ProfileEnvironmentVariableViewModel row) => EnvironmentVariables.Remove(row);

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginStatusLabel));
        OnPropertyChanged(nameof(LoginStatusBrushKey));
    }

    partial void OnSelectedProviderChanged(SessionProviderOption value)
    {
        OnPropertyChanged(nameof(IsClaudeProvider));
        OnPropertyChanged(nameof(IsLocalProvider));
        OnPropertyChanged(nameof(IsLmStudioProvider));
        OnPropertyChanged(nameof(IsPluginProvider));
        OnPropertyChanged(nameof(IsPluginProviderMissing));
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(BaseUrlPlaceholder));
        OnPropertyChanged(nameof(SupportsEnvVars));

        // Point the base URL at the newly chosen provider's default port when adding a profile — including
        // switching Ollama↔LM Studio (11434↔1234) — unless the operator typed a custom URL we should keep.
        if (CanChooseProvider && IsLocalProvider && (string.IsNullOrWhiteSpace(BaseUrl) || _IsAKnownDefaultUrl(BaseUrl)))
        {
            BaseUrl = SessionProviderCatalog.DefaultBaseUrl(value.Value);
        }

        // Rebuild the plugin config view for the newly chosen provider when adding a profile (the dropdown is
        // disabled otherwise, so this never fires for an already-created profile) — starts empty (no existing
        // config JSON yet) rather than carrying over the previous selection's view.
        if (CanChooseProvider)
        {
            PluginConfigView = value.Value == SessionProvider.Plugin && value.PluginProviderId is { } providerId
                ? _pluginProviderRegistry?.Resolve(providerId)?.CreateConfigView(null)
                : null;

            // A freshly added profile has no stored defaults yet — start each option on its own declared default.
            _RefreshPluginOptionDefaults(storedDefaults: null);
        }
    }

    /// <summary>
    /// Rebuilds <see cref="PluginOptionDefaults"/> from the selected plugin provider's declared launch options,
    /// each pre-filled from <paramref name="storedDefaults"/> (the profile's saved value) or the option's own
    /// declared default. Rendered the same generic way the New-session dialog renders a plugin's options, so a
    /// profile can remember its preferred permission mode/model/effort (Claude) or sandbox (Codex).
    /// </summary>
    private void _RefreshPluginOptionDefaults(IReadOnlyDictionary<string, string>? storedDefaults)
    {
        PluginOptionDefaults.Clear();

        if (_pluginProviderRegistry is not null
            && SelectedProvider.Value == SessionProvider.Plugin
            && SelectedProvider.PluginProviderId is { } providerId
            && _pluginProviderRegistry.Resolve(providerId) is { } registration)
        {
            foreach (var option in registration.Options)
            {
                var value = storedDefaults?.GetValueOrDefault(option.Key) ?? option.DefaultValue;
                PluginOptionDefaults.Add(new PluginTtyOptionSelectionViewModel(option.Key, option.Label, option.Choices, value, option.ChoiceLabels));
            }
        }

        OnPropertyChanged(nameof(HasPluginOptionDefaults));
    }

    partial void OnPluginConfigViewChanged(IPluginProviderConfigView? value) => OnPropertyChanged(nameof(IsPluginProviderMissing));

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));

    private static bool _IsAKnownDefaultUrl(string url) =>
        url == SessionProviderCatalog.DefaultBaseUrl(SessionProvider.Ollama)
        || url == SessionProviderCatalog.DefaultBaseUrl(SessionProvider.LmStudio);

    /// <param name="profile">The profile to edit.</param>
    /// <param name="isLoggedIn">Login status of the profile's config directory, evaluated once when the dialog loads.</param>
    /// <param name="canChooseProvider">Only a freshly added profile may pick its provider (#26).</param>
    /// <param name="providers">The full provider picker (#45) — built-ins plus any plugin-registered providers; falls back to <see cref="SessionProviderCatalog.Providers"/> (built-ins only) when not supplied.</param>
    /// <param name="pluginProviderRegistry">Resolves a plugin provider's config view, for a <see cref="PluginProviderConfig"/> profile or when the operator picks a plugin provider while adding one; <see langword="null"/> when the caller does not care about plugin providers (design-time preview, most existing tests).</param>
    /// <param name="availableMcpServerNames">The MCP servers (registry + plugin-provided) the profile may pre-select from (AC-130); <see langword="null"/>/empty hides the MCP pre-selection entirely (design-time preview, a caller that does not surface it).</param>
    public EditableProfileViewModel(
        SessionProfile profile,
        bool isLoggedIn,
        bool canChooseProvider = false,
        IReadOnlyList<SessionProviderOption>? providers = null,
        IPluginProviderRegistry? pluginProviderRegistry = null,
        IReadOnlyList<string>? availableMcpServerNames = null,
        IMcpToolTokenEstimator? tokenEstimator = null)
    {
        _tokenEstimator = tokenEstimator;
        _label = profile.Label;
        _configDir = profile.Claude?.ConfigDir ?? string.Empty;
        _executablePath = profile.Claude?.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _defaultWorkingDirectory = profile.DefaultWorkingDirectory ?? string.Empty;
        _profileSystemPrompt = profile.SystemPrompt ?? string.Empty;
        _memoryLimitMb = profile.MemoryLimitMb ?? 0;

        // MCP pre-selection (AC-130): a non-null saved set restricts; null means "all servers" (the gate stays off).
        // Each available server is ticked when the profile has no restriction or when its selection names the server.
        _restrictMcpServers = profile.EnabledMcpServerNames is not null;
        var available = new HashSet<string>(availableMcpServerNames ?? [], StringComparer.OrdinalIgnoreCase);
        var selected = profile.EnabledMcpServerNames is { } names ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase) : null;
        foreach (var name in availableMcpServerNames ?? [])
        {
            var item = new McpServerSelectionItemViewModel(name) { IsEnabledForSession = selected?.Contains(name) ?? true };
            item.PropertyChanged += _OnMcpServerRowChanged;
            McpServers.Add(item);
        }

        // If the profile already restricts its MCP servers, the pre-selection is shown from the start, so its
        // token estimate (AC-134) is worth computing now; otherwise it waits until the operator reveals it.
        if (_restrictMcpServers)
        {
            _ = _EstimateMcpTokensAsync(refresh: false);
        }

        // A saved server the catalog no longer offers (temporarily disabled, or a plugin not loaded right now) is not
        // shown, so the checklist cannot speak for it. Carry it through untouched on save rather than let a Save silently
        // drop a selection the operator can't even see here — the alternative wiped it the moment its server went absent.
        _carriedOverMcpServerNames = selected is null ? [] : [.. selected.Where(name => !available.Contains(name))];
        // The typed permission/model/effort selections back the retired Claude-CLI editor block (hidden now that Claude
        // is a plugin); a plugin profile's real defaults come from OptionDefaults via PluginOptionDefaults. Seed the
        // typed fields with the app defaults rather than the profile's legacy typed values, which are migration-only.
        _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;
        _claudeModel = SessionOptionCatalog.DefaultModel.Value;
        _selectedEffort = SessionOptionCatalog.DefaultEffort;
        _autoApproveTools = profile.Defaults?.AutoApproveTools ?? false;
        _selectedReadingLevel = SessionOptionCatalog.ResolveReadingLevel(profile.Defaults?.DefaultReadingLevel);

        var delegation = profile.DelegationPolicy;
        _allowedAsTarget = delegation.AllowedAsTarget;
        _delegationPurpose = delegation.Purpose ?? string.Empty;
        _allowedTaskTypes = delegation.AllowedTaskTypes is { Count: > 0 } types ? string.Join(", ", types) : string.Empty;
        _allowedWorkingDirs = delegation.AllowedWorkingDirs is { Count: > 0 } dirs ? string.Join(Environment.NewLine, dirs) : string.Empty;
        _maxConcurrentTasks = delegation.MaxConcurrent;
        _mayDelegateFurther = delegation.MayDelegateFurther;
        _delegationTimeoutMinutes = delegation.TimeoutMinutes;
        _permissionCeiling = delegation.PermissionCeiling;
        _allowedTools = delegation.AllowedTools is { Count: > 0 } allowedTools ? string.Join(Environment.NewLine, allowedTools) : string.Empty;

        _canChooseProvider = canChooseProvider;
        _isLoggedIn = isLoggedIn;
        _pluginProviderRegistry = pluginProviderRegistry;
        Providers = providers ?? SessionProviderCatalog.Providers;

        (_baseUrl, _model, _apiKey, _systemPrompt) = profile.ProviderConfig switch
        {
            OllamaConfig ollama => (ollama.BaseUrl, ollama.Model, string.Empty, ollama.SystemPrompt ?? string.Empty),
            LmStudioConfig lmStudio => (lmStudio.BaseUrl, lmStudio.Model, lmStudio.ApiKey ?? string.Empty, lmStudio.SystemPrompt ?? string.Empty),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty),
        };

        if (profile.ProviderConfig is PluginProviderConfig pluginConfig)
        {
            _selectedProvider = Providers.FirstOrDefault(option => option.Value == SessionProvider.Plugin && option.PluginProviderId == pluginConfig.ProviderId)
                ?? new SessionProviderOption($"Plugin ({pluginConfig.ProviderId})", SessionProvider.Plugin, pluginConfig.ProviderId);
            _pluginConfigView = pluginProviderRegistry?.Resolve(pluginConfig.ProviderId)?.CreateConfigView(pluginConfig.ConfigJson);

            // The provider plugin is not resolvable (removed/disabled/failed to load) — keep the raw config
            // so ToProfile can hand it back unchanged instead of collapsing to null (#45 review finding 1).
            if (_pluginConfigView is null)
            {
                _orphanedPluginConfig = pluginConfig;
            }
        }
        else
        {
            _selectedProvider = SessionProviderCatalog.Resolve(profile.Provider);
        }

        // Build the generic per-profile option-default editors from the (possibly plugin) provider, pre-filled from
        // the profile's saved defaults — the provider-neutral successor to the typed permission/model/effort combos.
        _RefreshPluginOptionDefaults(profile.Defaults?.OptionDefaults);

        foreach (var variable in profile.EnvironmentVariables ?? [])
        {
            EnvironmentVariables.Add(new ProfileEnvironmentVariableViewModel(variable.Key, variable.Value, variable.IsSecret));
        }
    }

    /// <summary>Rebuilds an immutable profile from the current edits, for persisting on save.</summary>
    public SessionProfile ToProfile()
    {
        // A plugin profile stores its start defaults only in the generic OptionDefaults map and writes the legacy
        // typed permission/model/effort fields blank, so those become a no-op the migration ignores on later loads —
        // OptionDefaults is the single source. A non-plugin provider (Ollama/LM Studio) keeps the legacy typed fields.
        var defaults = IsPluginProvider
            ? new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools) { OptionDefaults = _CollectPluginOptionDefaults() }
            : new ProfileDefaults(SelectedPermissionMode.Value, SessionOptionCatalog.ModelForValue(ClaudeModel).Value, SelectedEffort.Value, AutoApproveTools);
        // The reading level is provider-neutral (AC-138) — any profile can launch an SDK session — so it rides on
        // Defaults for both the plugin and the legacy-typed branch above rather than only one of them.
        defaults = defaults with { DefaultReadingLevel = SelectedReadingLevel.Value };

        return new(
            Label.Trim(),
            _ToProviderConfig(),
            string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(),
            defaults,
            _ToDelegationPolicy(),
            MemoryLimitMb >= SessionMemoryLimit.MinimumMegabytes ? MemoryLimitMb : null)
        {
            EnvironmentVariables = EnvironmentVariables.Count > 0
                ? [.. EnvironmentVariables.Select(row => row.ToDomain())]
                : null,
            // Off is "no restriction" — null, so every enabled server is ticked and future ones are included. On
            // persists the ticked servers plus any saved names the catalog didn't offer here (carried over untouched),
            // as this profile's explicit pre-selection.
            EnabledMcpServerNames = RestrictMcpServers
                ? [.. McpServers.Where(server => server.IsEnabledForSession).Select(server => server.Name), .. _carriedOverMcpServerNames]
                : null,
            DefaultWorkingDirectory = string.IsNullOrWhiteSpace(DefaultWorkingDirectory) ? null : DefaultWorkingDirectory.Trim(),
            SystemPrompt = string.IsNullOrWhiteSpace(ProfileSystemPrompt) ? null : ProfileSystemPrompt.Trim(),
        };
    }

    // The per-profile option defaults the operator set, keyed by option key; only the ones actually chosen (a blank
    // value leaves the option on the plugin's own default). Null when the provider declares no options.
    private IReadOnlyDictionary<string, string>? _CollectPluginOptionDefaults()
    {
        if (!HasPluginOptionDefaults)
        {
            return null;
        }

        var defaults = PluginOptionDefaults
            .Where(option => !string.IsNullOrWhiteSpace(option.Value))
            .ToDictionary(option => option.Key, option => option.Value!);
        return defaults.Count > 0 ? defaults : null;
    }

    // A profile that is not a target carries no policy at all, so cockpit.json stays quiet about the profiles
    // that have nothing to do with delegation.
    private DelegationPolicy? _ToDelegationPolicy()
    {
        if (!AllowedAsTarget)
        {
            return null;
        }

        var taskTypes = AllowedTaskTypes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // One directory per line; a comma is a legal character in a path, so it cannot be the separator here the way
        // it is for task types.
        var workingDirs = AllowedWorkingDirs
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // One tool name per line, same reasoning as working dirs (a tool name is free-form text).
        var allowedTools = AllowedTools
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // An empty/unrecognised ceiling falls back to the default rather than persisting a blank the decider would
        // read as the most restrictive.
        var ceiling = PermissionCeilingChoices.Contains(PermissionCeiling) ? PermissionCeiling : DelegationPolicy.DefaultPermissionCeiling;

        return new DelegationPolicy(
            AllowedAsTarget: true,
            MaxConcurrent: Math.Max(1, MaxConcurrentTasks),
            AllowedWorkingDirs: workingDirs.Count > 0 ? workingDirs : null,
            PermissionCeiling: ceiling,
            MayDelegateFurther: MayDelegateFurther,
            TimeoutMinutes: Math.Max(0, DelegationTimeoutMinutes),
            AllowedTaskTypes: taskTypes.Count > 0 ? taskTypes : null,
            Purpose: string.IsNullOrWhiteSpace(DelegationPurpose) ? null : DelegationPurpose.Trim(),
            Tags: null,
            AllowedTools: allowedTools.Count > 0 ? allowedTools : null);
    }

    private ProviderConfig _ToProviderConfig()
    {
        var systemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt.Trim();
        if (SelectedProvider.Value == SessionProvider.Plugin)
        {
            if (PluginConfigView is not null && PluginConfigView.TryGetConfigJson(out var configJson))
            {
                return new PluginProviderConfig(SelectedProvider.PluginProviderId ?? string.Empty, configJson);
            }

            // No config view to serialize (the provider plugin is not resolvable) — hand back the profile's
            // original config untouched rather than null, so a save/remove of some other row never silently
            // wipes this orphaned profile's ProviderId/ConfigJson (and any API key inside it). Reachable only
            // for a profile the ctor already flagged as orphaned (IsValid is false otherwise, so the
            // Manage-profiles Save gate never gets here without one) — a null here would be this view model
            // itself in a state its own invariants rule out, so it fails loudly instead of handing back a
            // profile with no provider at all.
            return _orphanedPluginConfig
                ?? throw new InvalidOperationException("Plugin provider selected with neither a config view nor an orphaned config to fall back to.");
        }

        return SelectedProvider.Value switch
        {
            SessionProvider.Ollama => new OllamaConfig(BaseUrl.Trim(), Model.Trim(), systemPrompt),
            SessionProvider.LmStudio => new LmStudioConfig(BaseUrl.Trim(), Model.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), systemPrompt),
            _ => new ClaudeConfig(ConfigDir.Trim(), string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim()),
        };
    }
}
