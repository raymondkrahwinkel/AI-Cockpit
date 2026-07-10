using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the Manage-profiles dialog (#12/#17): list the profiles, edit each one's label, config
/// directory (shown so it is clear where its login lives), executable, purpose and start defaults, and
/// add/remove entries. Save persists the whole edited list through <see cref="IClaudeProfileStore"/>;
/// the view closes via <see cref="CloseRequested"/>.
/// </summary>
public partial class ManageProfilesDialogViewModel : ViewModelBase
{
    private readonly IClaudeProfileStore? _profileStore;
    private readonly IClaudeProfileLoginChecker? _loginChecker;
    private readonly IModelCatalog? _modelCatalog;

    /// <summary>Raised when the dialog should close (after a save, or on cancel).</summary>
    public event Action? CloseRequested;

    public ObservableCollection<EditableProfileViewModel> Profiles { get; } = [];

    /// <summary>All four modes: a profile's default may be bypass, which the New-session dialog then offers at launch.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    public IReadOnlyList<ModelOption> Models => SessionOptionCatalog.Models;

    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private EditableProfileViewModel? _selectedProfile;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ManageProfilesDialogViewModel()
    {
        // Design-time preview: one editable profile so the dialog renders in the previewer.
        var sample = new EditableProfileViewModel(
            new ClaudeProfile("personal", "~/.claude-personal", Purpose: "private"), isLoggedIn: true);
        Profiles.Add(sample);
        SelectedProfile = sample;
    }

    public ManageProfilesDialogViewModel(IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker, IModelCatalog? modelCatalog = null)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _modelCatalog = modelCatalog;
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

        ModelFetchStatus = models.Count > 0 ? $"{models.Count} model(s)" : "No models found — is the server running?";
    }

    /// <summary>Loads the stored profiles into editable rows and selects the first.</summary>
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
            Profiles.Add(new EditableProfileViewModel(profile, _loginChecker?.IsLoggedIn(profile) ?? false));
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddProfile()
    {
        // A freshly added profile may pick its provider (#26); an existing one is fixed.
        var added = new EditableProfileViewModel(
            new ClaudeProfile("new profile", string.Empty), isLoggedIn: false, canChooseProvider: true);
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

        // A profile needs the fields its provider can launch with (a config directory for Claude, a base
        // URL and model for a local provider); refuse to persist a half-filled row rather than write junk.
        if (Profiles.Any(profile => !profile.IsValid))
        {
            StatusMessage = "Every profile needs a label, plus a config directory (Claude) or a base URL and model (local).";
            return;
        }

        var profiles = Profiles.Select(profile => profile.ToProfile()).ToList();
        await _profileStore.SaveAsync(profiles);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
