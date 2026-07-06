using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the New-session dialog (#31/#17/#15): pick a profile, and — for an SDK session — its start
/// mode/model/effort. Choosing a profile loads its saved defaults (<see cref="ProfileDefaults"/>),
/// which the operator can still override before starting. Mode here offers all four modes including
/// bypass, since bypass is launch-only and this dialog is the one place it can be chosen. The view
/// closes via <see cref="CloseRequested"/>, carrying the choices on confirm or null on cancel.
/// </summary>
public partial class NewSessionDialogViewModel : ViewModelBase
{
    private readonly IClaudeProfileLoginChecker? _loginChecker;
    private readonly IClaudeProfileStore? _profileStore;

    /// <summary>Raised when the dialog should close: the result carries the confirmed choices, or null on cancel.</summary>
    public event Action<NewSessionResult?>? CloseRequested;

    /// <summary>Raised when the operator wants to manage profiles; the host opens the Manage-profiles dialog and reloads.</summary>
    public event Action? ManageProfilesRequested;

    public SessionKind Kind { get; }

    /// <summary>Window title: SDK vs TTY variant.</summary>
    public string HeaderText => Kind == SessionKind.Sdk ? "New session" : "New session (TTY)";

    /// <summary>
    /// SDK sessions launch with a mode/model/effort; a TTY session has none of those launch options
    /// (the real TUI owns them), so the dialog hides that block rather than showing dead controls.
    /// </summary>
    public bool ShowSessionOptions => Kind == SessionKind.Sdk;

    public ObservableCollection<ClaudeProfile> Profiles { get; } = [];

    /// <summary>All four modes — including the launch-only bypass — since this dialog is the one place bypass can be chosen.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    public IReadOnlyList<ModelOption> Models => SessionOptionCatalog.Models;

    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    [ObservableProperty]
    private ClaudeProfile? _selectedProfile;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = SessionOptionCatalog.DefaultPermissionMode;

    [ObservableProperty]
    private ModelOption _selectedModel = SessionOptionCatalog.DefaultModel;

    [ObservableProperty]
    private EffortOption _selectedEffort = SessionOptionCatalog.DefaultEffort;

    [ObservableProperty]
    private bool _isSelectedProfileLoggedIn;

    /// <summary>Config directory of the selected profile, shown under the picker so it is clear where its login lives.</summary>
    public string? SelectedProfileConfigDir => SelectedProfile?.ConfigDir;

    public string LoginStatusLabel => IsSelectedProfileLoggedIn ? "logged in" : "not logged in";

    /// <summary>Guidance shown (in the body) only when the selected profile isn't logged in.</summary>
    public bool ShowLoginHint => SelectedProfile is not null && !IsSelectedProfileLoggedIn;

    public string LoginStatusBrushKey => IsSelectedProfileLoggedIn
        ? "CockpitStatusDoneBrush"
        : "CockpitStatusWaitingBrush";

    /// <summary>Start is only offered for a logged-in profile: launching an unauthenticated one would just fail (no dead control).</summary>
    public bool CanStart => SelectedProfile is not null && IsSelectedProfileLoggedIn;

    // Design-time constructor for the Avalonia previewer: one logged-in profile so the dialog renders.
    public NewSessionDialogViewModel()
    {
        Kind = SessionKind.Sdk;
        var personal = new ClaudeProfile("personal", "~/.claude-personal", Purpose: "private");
        Profiles.Add(personal);
        SelectedProfile = personal;
        IsSelectedProfileLoggedIn = true;
    }

    public NewSessionDialogViewModel(IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker, SessionKind kind)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        Kind = kind;
    }

    /// <summary>Loads the profiles and selects the first, so the dialog opens ready to confirm.</summary>
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
    }

    partial void OnSelectedProfileChanged(ClaudeProfile? value)
    {
        IsSelectedProfileLoggedIn = value is not null && (_loginChecker?.IsLoggedIn(value) ?? false);
        OnPropertyChanged(nameof(SelectedProfileConfigDir));

        // Choosing a profile loads its saved start defaults (or the app defaults when it has none),
        // which the operator can still override before starting.
        var defaults = value?.Defaults;
        SelectedPermissionMode = SessionOptionCatalog.ResolvePermissionMode(defaults?.PermissionMode);
        SelectedModel = SessionOptionCatalog.ResolveModel(defaults?.Model);
        SelectedEffort = SessionOptionCatalog.ResolveEffort(defaults?.Effort);

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSelectedProfileLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(LoginStatusLabel));
        OnPropertyChanged(nameof(LoginStatusBrushKey));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowLoginHint));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Confirm()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        CloseRequested?.Invoke(new NewSessionResult(SelectedProfile, SelectedPermissionMode, SelectedModel, SelectedEffort));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void ManageProfiles() => ManageProfilesRequested?.Invoke();
}
