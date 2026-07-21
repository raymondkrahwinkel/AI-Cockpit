using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the Manage-profiles dialog (#12/#17): list the profiles, edit each one's label, config
/// directory (shown so it is clear where its login lives), executable, purpose and start defaults, and
/// add/remove entries. Save persists the whole edited list through <see cref="ISessionProfileStore"/>;
/// the view closes via <see cref="CloseRequested"/>.
/// </summary>
public partial class ManageProfilesDialogViewModel : ViewModelBase
{
    private readonly ISessionProfileStore? _profileStore;
    private readonly IProfileLoginChecker? _loginChecker;
    private readonly IModelCatalog? _modelCatalog;
    private readonly IPluginProviderRegistry? _pluginProviderRegistry;
    private readonly IMcpServerCatalog? _mcpServerCatalog;
    private readonly IMcpToolTokenEstimator? _tokenEstimator;
    private readonly IReadOnlyList<SessionProviderOption> _providers;

    /// <summary>The MCP servers a profile may pre-select from (AC-130), fetched once when the dialog loads; empty until then, or when no catalog was supplied.</summary>
    private IReadOnlyList<string> _availableMcpServerNames = [];

    /// <summary>Raised when the dialog should close (after a save, or on cancel).</summary>
    public event Action? CloseRequested;

    public ObservableCollection<EditableProfileViewModel> Profiles { get; } = [];

    /// <summary>All four modes: a profile's default may be bypass, which the New-session dialog then offers at launch.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    // The Claude model field is now an editable AutoCompleteBox bound to each profile's own ClaudeModelSuggestions
    // (EditableProfileViewModel), so the dialog no longer exposes a shared ModelOption list here.
    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EditableProfileViewModel? _selectedProfile;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ManageProfilesDialogViewModel()
    {
        _providers = SessionProviderCatalog.Providers;

        // Design-time preview: one editable profile so the dialog renders in the previewer — a delegation target,
        // so the whole form (including the delegation fields, which are hidden until a profile is one) shows up.
        var sample = new EditableProfileViewModel(
            new SessionProfile(
                "local",
                new OllamaConfig("http://localhost:11434", "Qwen2.5-Coder:7b", null),
                Purpose: "cheap local model",
                Delegation: new DelegationPolicy(
                    AllowedAsTarget: true,
                    MaxConcurrent: 2,
                    AllowedTaskTypes: ["summarize", "refactor"],
                    Purpose: "cheap bulk refactors and summarising — no web access")),
            isLoggedIn: true);
        Profiles.Add(sample);
        SelectedProfile = sample;
    }

    public ManageProfilesDialogViewModel(
        ISessionProfileStore profileStore,
        IProfileLoginChecker loginChecker,
        IModelCatalog? modelCatalog = null,
        IPluginProviderRegistry? pluginProviderRegistry = null,
        IMcpServerCatalog? mcpServerCatalog = null,
        IMcpToolTokenEstimator? tokenEstimator = null)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _modelCatalog = modelCatalog;
        _pluginProviderRegistry = pluginProviderRegistry;
        _mcpServerCatalog = mcpServerCatalog;
        _tokenEstimator = tokenEstimator;

        // Snapshot the plugin-registered providers once per dialog open (#45) — registrations only ever
        // happen at plugin-load time, well before this dialog can be shown, so a live-updating list buys
        // nothing here.
        _providers = pluginProviderRegistry is null ? SessionProviderCatalog.Providers : SessionProviderCatalog.AllProviders(pluginProviderRegistry);
    }

    /// <summary>Status of the last model refresh (count or a "server not running" hint), shown next to the model picker.</summary>
    [ObservableProperty]
    private string _modelFetchStatus = string.Empty;

    /// <summary>Fetches the selected local profile's installed models from its server so the operator can pick one instead of typing an id (#26).</summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (_modelCatalog is null || SelectedProfile is not { IsLocalProvider: true } profile)
        {
            return;
        }

        ModelFetchStatus = "Loading…";
        var models = await _modelCatalog.ListModelsAsync(profile.BaseUrl, string.IsNullOrWhiteSpace(profile.ApiKey) ? null : profile.ApiKey);
        profile.AvailableModels.Clear();
        foreach (var model in models)
        {
            profile.AvailableModels.Add(model);
        }

        if (models.Count == 0)
        {
            ModelFetchStatus = "No models found — is the server running?";
            return;
        }

        // Pre-fill the first model when the field is still empty, so a fetch gives immediate, visible
        // confirmation; the operator can then click the field to pick a different one.
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            profile.Model = models[0];
        }

        ModelFetchStatus = $"Found {models.Count} model(s) — click the field to pick another, or type an id";
    }

    /// <summary>Loads the stored profiles into editable rows and selects the first.</summary>
    public async Task LoadAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        // The MCP catalog for the per-profile pre-selection (AC-130) — registry plus each active plugin's own
        // servers, the same set the New-session checklist offers. Fetched once here so every row shares it.
        _availableMcpServerNames = _mcpServerCatalog is null
            ? []
            : [.. (await _mcpServerCatalog.GetServersAsync()).Where(server => server.Enabled).Select(server => server.Name)];

        var profiles = await _profileStore.LoadAsync();
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new EditableProfileViewModel(profile, _loginChecker?.IsLoggedIn(profile) ?? false, providers: _providers, pluginProviderRegistry: _pluginProviderRegistry, availableMcpServerNames: _availableMcpServerNames, tokenEstimator: _tokenEstimator));
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddProfile()
    {
        // A freshly added profile may pick its provider (#26); an existing one is fixed. Defaults to the bundled
        // Claude provider plugin — Claude is a plugin like every other now (Fase 4), not a built-in CLI provider.
        var added = new EditableProfileViewModel(
            new SessionProfile("new profile", ClaudePluginProfile.Create(string.Empty, null)), isLoggedIn: false, canChooseProvider: true, providers: _providers, pluginProviderRegistry: _pluginProviderRegistry, availableMcpServerNames: _availableMcpServerNames, tokenEstimator: _tokenEstimator);
        Profiles.Add(added);
        SelectedProfile = added;
    }

    /// <summary>True while a remove is awaiting confirmation, so the footer shows a "Remove 'X'?" prompt.</summary>
    [ObservableProperty]
    private bool _isConfirmingRemove;

    /// <summary>Label of the profile pending removal, for the confirmation prompt.</summary>
    [ObservableProperty]
    private string _pendingRemovalLabel = string.Empty;

    /// <summary>Starts a remove: asks for confirmation rather than dropping the row silently.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        PendingRemovalLabel = SelectedProfile.Label;
        IsConfirmingRemove = true;
    }

    /// <summary>
    /// Confirms the remove: drops the selected profile and persists the reduced list immediately, so a
    /// removal takes effect without a separate Save (and isn't lost if another row is mid-edit).
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsConfirmingRemove = false;
        if (SelectedProfile is null)
        {
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];

        if (_profileStore is not null)
        {
            await _profileStore.SaveAsync(Profiles.Select(profile => profile.ToProfile()).ToList());
            StatusMessage = "Removed.";
        }
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    private bool HasSelectedProfile => SelectedProfile is not null;

    partial void OnSelectedProfileChanged(EditableProfileViewModel? value)
    {
        RemoveProfileCommand.NotifyCanExecuteChanged();
        IsConfirmingRemove = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        // A profile needs the settings its own provider launches with; refuse to persist a half-filled row rather
        // than write junk. The message names the profiles rather than the fields: which fields those are is the
        // provider's business, and a message that enumerates them ("a config directory, or a base URL and model")
        // is one that quietly becomes a lie with every provider added — as it already had for Codex, which needs
        // neither.
        if (Profiles.Where(profile => !profile.IsValid).Select(profile => profile.Label).ToList() is { Count: > 0 } incomplete)
        {
            var named = incomplete.Select(label => string.IsNullOrWhiteSpace(label) ? "(unnamed)" : label);
            StatusMessage = $"Fill in what these profiles' providers need: {string.Join(", ", named)}.";
            return;
        }

        var profiles = Profiles.Select(profile => profile.ToProfile()).ToList();
        await _profileStore.SaveAsync(profiles);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
