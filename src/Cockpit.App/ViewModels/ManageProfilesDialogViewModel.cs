using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public ManageProfilesDialogViewModel(IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
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
        var added = new EditableProfileViewModel(
            new ClaudeProfile("new profile", string.Empty), isLoggedIn: false);
        Profiles.Add(added);
        SelectedProfile = added;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    private bool HasSelectedProfile => SelectedProfile is not null;

    partial void OnSelectedProfileChanged(EditableProfileViewModel? value) => RemoveProfileCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_profileStore is null)
        {
            return;
        }

        // A profile needs at least a label and a config directory to be usable; refuse to persist a
        // half-filled row (e.g. a freshly added one) rather than write junk the picker can't launch.
        if (Profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Label) || string.IsNullOrWhiteSpace(profile.ConfigDir)))
        {
            StatusMessage = "Every profile needs a label and a config directory.";
            return;
        }

        var profiles = Profiles.Select(profile => profile.ToProfile()).ToList();
        await _profileStore.SaveAsync(profiles);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
