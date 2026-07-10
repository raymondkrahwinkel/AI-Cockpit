using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Profiles;

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

    /// <summary>Login status of this profile's config directory, evaluated once when the dialog loads.</summary>
    [ObservableProperty]
    private bool _isLoggedIn;

    public IReadOnlyList<SessionProviderOption> Providers => SessionProviderCatalog.Providers;

    public bool IsClaudeProvider => SelectedProvider.Value == SessionProvider.ClaudeCli;

    public bool IsLocalProvider => !IsClaudeProvider;

    public bool IsLmStudioProvider => SelectedProvider.Value == SessionProvider.LmStudio;

    public string LoginStatusLabel => IsLoggedIn ? "logged in" : "not logged in";

    public string LoginStatusBrushKey => IsLoggedIn ? "CockpitStatusDoneBrush" : "CockpitStatusWaitingBrush";

    /// <summary>Whether this row has the fields its provider needs to launch — a label always, plus a config directory (Claude) or a base URL and model (local).</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Label)
        && (IsClaudeProvider
            ? !string.IsNullOrWhiteSpace(ConfigDir)
            : !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model));

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

        // Pre-fill the local server URL when a fresh profile switches to a local provider, so the operator
        // starts from the usual localhost address rather than an empty field.
        if (CanChooseProvider && IsLocalProvider && string.IsNullOrWhiteSpace(BaseUrl))
        {
            BaseUrl = SessionProviderCatalog.DefaultBaseUrl(value.Value);
        }
    }

    public EditableProfileViewModel(ClaudeProfile profile, bool isLoggedIn, bool canChooseProvider = false)
    {
        _label = profile.Label;
        _configDir = profile.ConfigDir;
        _executablePath = profile.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _selectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(profile.Defaults?.PermissionMode);
        _selectedModel = SessionOptionCatalog.ResolveModel(profile.Defaults?.Model);
        _selectedEffort = SessionOptionCatalog.ResolveEffort(profile.Defaults?.Effort);
        _selectedProvider = SessionProviderCatalog.Resolve(profile.Provider);
        _canChooseProvider = canChooseProvider;
        _isLoggedIn = isLoggedIn;

        (_baseUrl, _model, _apiKey) = profile.ProviderConfig switch
        {
            OllamaConfig ollama => (ollama.BaseUrl, ollama.Model, string.Empty),
            LmStudioConfig lmStudio => (lmStudio.BaseUrl, lmStudio.Model, lmStudio.ApiKey ?? string.Empty),
            _ => (string.Empty, string.Empty, string.Empty),
        };
    }

    /// <summary>Rebuilds an immutable profile from the current edits, for persisting on save.</summary>
    public ClaudeProfile ToProfile() => new(
        Label.Trim(),
        ConfigDir.Trim(),
        string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
        string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(),
        new ProfileDefaults(SelectedPermissionMode.Value, SelectedModel.Value, SelectedEffort.Value),
        _ToProviderConfig());

    private ProviderConfig? _ToProviderConfig() => SelectedProvider.Value switch
    {
        SessionProvider.Ollama => new OllamaConfig(BaseUrl.Trim(), Model.Trim()),
        SessionProvider.LmStudio => new LmStudioConfig(BaseUrl.Trim(), Model.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim()),
        _ => null,
    };
}
