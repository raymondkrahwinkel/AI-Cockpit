using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A mutable, editable view over an immutable <see cref="ClaudeProfile"/> for the Manage-profiles
/// dialog: the record's fields as editable properties plus its <see cref="ProfileDefaults"/> as three
/// selected options. <see cref="ToProfile"/> turns the edits back into a profile on save; empty
/// executable/purpose collapse to <see langword="null"/> so an unset field stays unset.
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

    /// <summary>Login status of this profile's config directory, evaluated once when the dialog loads.</summary>
    [ObservableProperty]
    private bool _isLoggedIn;

    public string LoginStatusLabel => IsLoggedIn ? "logged in" : "not logged in";

    public string LoginStatusBrushKey => IsLoggedIn ? "CockpitStatusDoneBrush" : "CockpitStatusWaitingBrush";

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginStatusLabel));
        OnPropertyChanged(nameof(LoginStatusBrushKey));
    }

    public EditableProfileViewModel(ClaudeProfile profile, bool isLoggedIn)
    {
        _label = profile.Label;
        _configDir = profile.ConfigDir;
        _executablePath = profile.ExecutablePath ?? string.Empty;
        _purpose = profile.Purpose ?? string.Empty;
        _selectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(profile.Defaults?.PermissionMode);
        _selectedModel = SessionOptionCatalog.ResolveModel(profile.Defaults?.Model);
        _selectedEffort = SessionOptionCatalog.ResolveEffort(profile.Defaults?.Effort);
        _isLoggedIn = isLoggedIn;
    }

    /// <summary>Rebuilds an immutable profile from the current edits, for persisting on save.</summary>
    public ClaudeProfile ToProfile() => new(
        Label.Trim(),
        ConfigDir.Trim(),
        string.IsNullOrWhiteSpace(ExecutablePath) ? null : ExecutablePath.Trim(),
        string.IsNullOrWhiteSpace(Purpose) ? null : Purpose.Trim(),
        new ProfileDefaults(SelectedPermissionMode.Value, SelectedModel.Value, SelectedEffort.Value));
}
