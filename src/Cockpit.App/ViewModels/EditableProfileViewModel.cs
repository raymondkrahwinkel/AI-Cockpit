using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
        && SelectedProvider.Value switch
        {
            SessionProvider.ClaudeCli => !string.IsNullOrWhiteSpace(ConfigDir),
            SessionProvider.Plugin => PluginConfigView is not null && PluginConfigView.TryGetConfigJson(out _),
            _ => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model),
        };

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
        }
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
    public EditableProfileViewModel(
        SessionProfile profile,
        bool isLoggedIn,
        bool canChooseProvider = false,
        IReadOnlyList<SessionProviderOption>? providers = null,
        IPluginProviderRegistry? pluginProviderRegistry = null)
    {
        _label = profile.Label;
        _configDir = profile.Claude?.ConfigDir ?? string.Empty;
        _executablePath = profile.Claude?.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _memoryLimitMb = profile.MemoryLimitMb ?? 0;
        _selectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(profile.Defaults?.PermissionMode);
        _claudeModel = string.IsNullOrWhiteSpace(profile.Defaults?.Model) ? SessionOptionCatalog.DefaultModel.Value : profile.Defaults.Model;
        _selectedEffort = SessionOptionCatalog.ResolveEffort(profile.Defaults?.Effort);
        _autoApproveTools = profile.Defaults?.AutoApproveTools ?? false;

        var delegation = profile.DelegationPolicy;
        _allowedAsTarget = delegation.AllowedAsTarget;
        _delegationPurpose = delegation.Purpose ?? string.Empty;
        _allowedTaskTypes = delegation.AllowedTaskTypes is { Count: > 0 } types ? string.Join(", ", types) : string.Empty;
        _allowedWorkingDirs = delegation.AllowedWorkingDirs is { Count: > 0 } dirs ? string.Join(Environment.NewLine, dirs) : string.Empty;
        _maxConcurrentTasks = delegation.MaxConcurrent;
        _mayDelegateFurther = delegation.MayDelegateFurther;
        _delegationTimeoutMinutes = delegation.TimeoutMinutes;

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
    }

    /// <summary>Rebuilds an immutable profile from the current edits, for persisting on save.</summary>
    public SessionProfile ToProfile() => new(
        Label.Trim(),
        _ToProviderConfig(),
        string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(),
        new ProfileDefaults(SelectedPermissionMode.Value, SessionOptionCatalog.ModelForValue(ClaudeModel).Value, SelectedEffort.Value, AutoApproveTools),
        _ToDelegationPolicy(),
        MemoryLimitMb >= SessionMemoryLimit.MinimumMegabytes ? MemoryLimitMb : null);

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

        return new DelegationPolicy(
            AllowedAsTarget: true,
            MaxConcurrent: Math.Max(1, MaxConcurrentTasks),
            AllowedWorkingDirs: workingDirs.Count > 0 ? workingDirs : null,
            PermissionCeiling: DelegationPolicy.DefaultPermissionCeiling,
            MayDelegateFurther: MayDelegateFurther,
            TimeoutMinutes: Math.Max(0, DelegationTimeoutMinutes),
            AllowedTaskTypes: taskTypes.Count > 0 ? taskTypes : null,
            Purpose: string.IsNullOrWhiteSpace(DelegationPurpose) ? null : DelegationPurpose.Trim(),
            Tags: null);
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
