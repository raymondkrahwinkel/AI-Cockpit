using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A mutable, editable view over an immutable <see cref="ClaudeProfile"/> for the Manage-profiles
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

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode;

    [ObservableProperty]
    private ModelOption _selectedModel;

    [ObservableProperty]
    private EffortOption _selectedEffort;

    /// <summary>Whether a session under this profile starts with "allow all tools" already on (#26) — only meaningful for a local provider, which gates tool calls per-call rather than through Claude's permission modes.</summary>
    [ObservableProperty]
    private bool _autoApproveTools;

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

    public IReadOnlyList<SessionProviderOption> Providers { get; }

    /// <summary>Models the local server reported on the last refresh, offered as suggestions in the model picker.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

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
        ClaudeProfile profile,
        bool isLoggedIn,
        bool canChooseProvider = false,
        IReadOnlyList<SessionProviderOption>? providers = null,
        IPluginProviderRegistry? pluginProviderRegistry = null)
    {
        _label = profile.Label;
        _configDir = profile.ConfigDir;
        _executablePath = profile.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _selectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(profile.Defaults?.PermissionMode);
        _selectedModel = SessionOptionCatalog.ResolveModel(profile.Defaults?.Model);
        _selectedEffort = SessionOptionCatalog.ResolveEffort(profile.Defaults?.Effort);
        _autoApproveTools = profile.Defaults?.AutoApproveTools ?? false;
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
        }
        else
        {
            _selectedProvider = SessionProviderCatalog.Resolve(profile.Provider);
        }
    }

    /// <summary>Rebuilds an immutable profile from the current edits, for persisting on save.</summary>
    public ClaudeProfile ToProfile() => new(
        Label.Trim(),
        ConfigDir.Trim(),
        string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
        string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(),
        new ProfileDefaults(SelectedPermissionMode.Value, SelectedModel.Value, SelectedEffort.Value, AutoApproveTools),
        _ToProviderConfig());

    private ProviderConfig? _ToProviderConfig()
    {
        var systemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt.Trim();
        if (SelectedProvider.Value == SessionProvider.Plugin)
        {
            return PluginConfigView is not null && PluginConfigView.TryGetConfigJson(out var configJson)
                ? new PluginProviderConfig(SelectedProvider.PluginProviderId ?? string.Empty, configJson)
                : null;
        }

        return SelectedProvider.Value switch
        {
            SessionProvider.Ollama => new OllamaConfig(BaseUrl.Trim(), Model.Trim(), systemPrompt),
            SessionProvider.LmStudio => new LmStudioConfig(BaseUrl.Trim(), Model.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), systemPrompt),
            _ => null,
        };
    }
}
