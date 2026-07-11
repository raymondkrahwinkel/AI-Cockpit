using System.Collections.ObjectModel;
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

    public IReadOnlyList<SessionProviderOption> Providers => SessionProviderCatalog.Providers;

    /// <summary>Models the local server reported on the last refresh, offered as suggestions in the model picker.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    public bool IsClaudeProvider => SelectedProvider.Value == SessionProvider.ClaudeCli;

    public bool IsLocalProvider => !IsClaudeProvider;

    public bool IsLmStudioProvider => SelectedProvider.Value == SessionProvider.LmStudio;

    /// <summary>Label shown in the profile list, with the provider (and local model) appended (#26).</summary>
    public string DisplayLabel => ProfileDisplay.Format(Label, SelectedProvider.Value, Model);

    /// <summary>Placeholder for the base-URL field, defaulting to the selected local provider's usual localhost address.</summary>
    public string BaseUrlPlaceholder => SessionProviderCatalog.DefaultBaseUrl(SelectedProvider.Value);

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
        OnPropertyChanged(nameof(DisplayLabel));
        OnPropertyChanged(nameof(BaseUrlPlaceholder));

        // Point the base URL at the newly chosen provider's default port when adding a profile — including
        // switching Ollama↔LM Studio (11434↔1234) — unless the operator typed a custom URL we should keep.
        if (CanChooseProvider && IsLocalProvider && (string.IsNullOrWhiteSpace(BaseUrl) || _IsAKnownDefaultUrl(BaseUrl)))
        {
            BaseUrl = SessionProviderCatalog.DefaultBaseUrl(value.Value);
        }
    }

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(DisplayLabel));

    private static bool _IsAKnownDefaultUrl(string url) =>
        url == SessionProviderCatalog.DefaultBaseUrl(SessionProvider.Ollama)
        || url == SessionProviderCatalog.DefaultBaseUrl(SessionProvider.LmStudio);

    public EditableProfileViewModel(ClaudeProfile profile, bool isLoggedIn, bool canChooseProvider = false)
    {
        _label = profile.Label;
        _configDir = profile.ConfigDir;
        _executablePath = profile.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _selectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(profile.Defaults?.PermissionMode);
        _selectedModel = SessionOptionCatalog.ResolveModel(profile.Defaults?.Model);
        _selectedEffort = SessionOptionCatalog.ResolveEffort(profile.Defaults?.Effort);
        _autoApproveTools = profile.Defaults?.AutoApproveTools ?? false;
        _selectedProvider = SessionProviderCatalog.Resolve(profile.Provider);
        _canChooseProvider = canChooseProvider;
        _isLoggedIn = isLoggedIn;

        (_baseUrl, _model, _apiKey, _systemPrompt) = profile.ProviderConfig switch
        {
            OllamaConfig ollama => (ollama.BaseUrl, ollama.Model, string.Empty, ollama.SystemPrompt ?? string.Empty),
            LmStudioConfig lmStudio => (lmStudio.BaseUrl, lmStudio.Model, lmStudio.ApiKey ?? string.Empty, lmStudio.SystemPrompt ?? string.Empty),
            _ => (string.Empty, string.Empty, string.Empty, string.Empty),
        };
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
        return SelectedProvider.Value switch
        {
            SessionProvider.Ollama => new OllamaConfig(BaseUrl.Trim(), Model.Trim(), systemPrompt),
            SessionProvider.LmStudio => new LmStudioConfig(BaseUrl.Trim(), Model.Trim(), string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), systemPrompt),
            _ => null,
        };
    }
}
