using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the New-session dialog (#31/#17/#15/#32): pick the session kind (SDK vs TTY), a profile, and
/// its start mode/model/effort. Choosing a profile loads its saved defaults (<see cref="ProfileDefaults"/>),
/// which the operator can still override before starting. Mode here offers all four modes including
/// bypass, since bypass is launch-only and this dialog is the one place it can be chosen. The view
/// closes via <see cref="CloseRequested"/>, carrying the choices on confirm or null on cancel.
/// </summary>
/// <remarks>
/// <see cref="SelectedKind"/> plus the <see cref="IsSdk"/>/<see cref="IsTty"/> computed pair is the
/// mechanism for showing/hiding kind-specific fields: today only the hint text differs (see
/// <c>NewSessionDialog.axaml</c>), but a later kind-specific field just adds another
/// <c>IsVisible="{Binding IsSdk}"</c>/<c>IsTty</c> binding without touching the rest of the dialog.
/// </remarks>
public partial class NewSessionDialogViewModel : ViewModelBase
{
    private readonly IClaudeProfileLoginChecker? _loginChecker;
    private readonly IClaudeProfileStore? _profileStore;

    /// <summary>Raised when the dialog should close: the result carries the confirmed choices, or null on cancel.</summary>
    public event Action<NewSessionResult?>? CloseRequested;

    /// <summary>Raised when the operator wants to manage profiles; the host opens the Manage-profiles dialog and reloads.</summary>
    public event Action? ManageProfilesRequested;

    /// <summary>Which kind of session to create; chosen in the dialog itself (#32), defaulting to TTY (Raymond's preferred default).</summary>
    [ObservableProperty]
    private SessionKind _selectedKind = SessionKind.Tty;

    public bool IsSdk => SelectedKind == SessionKind.Sdk;

    public bool IsTty => SelectedKind == SessionKind.Tty;

    /// <summary>Window title, tracking the chosen kind.</summary>
    public string HeaderText => IsSdk ? "New session" : "New session (TTY)";

    /// <summary>
    /// The mode/model/effort options are Claude-CLI concepts, shown only for a Claude profile — TTY passes
    /// them as launch-only CLI flags, SDK keeps them live-switchable. A local provider has none of these,
    /// so the whole block is hidden for it (#26).
    /// </summary>
    public bool ShowSessionOptions => IsClaudeProfile;

    /// <summary>The SDK "stays live-switchable" hint, shown only for a Claude SDK session (a local session has no such dropdowns).</summary>
    public bool ShowSdkStartHint => IsSdk && IsClaudeProfile;

    /// <summary>The TTY "start defaults only" hint, shown only for a Claude TTY session.</summary>
    public bool ShowTtyStartHint => IsTty && IsClaudeProfile;

    public ObservableCollection<ClaudeProfile> Profiles { get; } = [];

    /// <summary>All four modes — including the launch-only bypass — since this dialog is the one place bypass can be chosen.</summary>
    public IReadOnlyList<PermissionModeOption> PermissionModes => SessionOptionCatalog.AllPermissionModes;

    public IReadOnlyList<ModelOption> Models => SessionOptionCatalog.Models;

    public IReadOnlyList<EffortOption> Efforts => SessionOptionCatalog.Efforts;

    /// <summary>Optional friendly name for the session, shown in the sidebar and above the panel; blank falls back to "Claude N".</summary>
    [ObservableProperty]
    private string _sessionName = string.Empty;

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

    /// <summary>Whether the selected profile runs on the Claude CLI (login + session-option/config fields apply) versus a local HTTP provider (#26).</summary>
    public bool IsClaudeProfile => SelectedProfile?.Provider is null or SessionProvider.ClaudeCli;

    public bool IsLocalProfile => SelectedProfile is not null && !IsClaudeProfile;

    /// <summary>Provider label shown next to the picker; empty for Claude, which needs no badge.</summary>
    public string SelectedProviderLabel => IsLocalProfile ? SessionProviderCatalog.Resolve(SelectedProfile!.Provider).Label : string.Empty;

    public string LoginStatusLabel => IsSelectedProfileLoggedIn ? "logged in" : "not logged in";

    /// <summary>Guidance shown (in the body) only when the selected Claude profile isn't logged in — a local provider has no login.</summary>
    public bool ShowLoginHint => IsClaudeProfile && SelectedProfile is not null && !IsSelectedProfileLoggedIn;

    public string LoginStatusBrushKey => IsSelectedProfileLoggedIn
        ? "CockpitStatusDoneBrush"
        : "CockpitStatusWaitingBrush";

    /// <summary>A Claude profile is only startable once logged in (launching unauthenticated would just fail); a local profile has no login, so it starts as soon as it is selected.</summary>
    public bool CanStart => SelectedProfile is not null && (IsLocalProfile || IsSelectedProfileLoggedIn);

    // Design-time constructor for the Avalonia previewer: one logged-in profile so the dialog renders.
    public NewSessionDialogViewModel()
    {
        var personal = new ClaudeProfile("personal", "~/.claude-personal", Purpose: "private");
        Profiles.Add(personal);
        SelectedProfile = personal;
        IsSelectedProfileLoggedIn = true;
    }

    public NewSessionDialogViewModel(IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _profileStore = profileStore;
        _loginChecker = loginChecker;
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
        // A local provider has no Claude login concept — treat it as "logged in" so login gating never blocks it.
        var isClaudeProfile = value?.Provider is null or SessionProvider.ClaudeCli;
        IsSelectedProfileLoggedIn = value is not null && (!isClaudeProfile || (_loginChecker?.IsLoggedIn(value) ?? false));
        OnPropertyChanged(nameof(SelectedProfileConfigDir));
        OnPropertyChanged(nameof(IsClaudeProfile));
        OnPropertyChanged(nameof(IsLocalProfile));
        OnPropertyChanged(nameof(SelectedProviderLabel));

        // A local provider only runs as an SDK session (TTY spawns the claude CLI, which is Claude-only),
        // so force SDK and let the view hide the kind selector for it.
        if (value?.Provider is SessionProvider.Ollama or SessionProvider.LmStudio)
        {
            SelectedKind = SessionKind.Sdk;
        }

        OnPropertyChanged(nameof(ShowSessionOptions));
        OnPropertyChanged(nameof(ShowSdkStartHint));
        OnPropertyChanged(nameof(ShowTtyStartHint));

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

    partial void OnSelectedKindChanged(SessionKind value)
    {
        OnPropertyChanged(nameof(IsSdk));
        OnPropertyChanged(nameof(IsTty));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(ShowSdkStartHint));
        OnPropertyChanged(nameof(ShowTtyStartHint));
    }

    [RelayCommand]
    private void SelectSdk() => SelectedKind = SessionKind.Sdk;

    [RelayCommand]
    private void SelectTty() => SelectedKind = SessionKind.Tty;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Confirm()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(SessionName) ? null : SessionName.Trim();
        CloseRequested?.Invoke(new NewSessionResult(SelectedKind, SelectedProfile, SelectedPermissionMode, SelectedModel, SelectedEffort, name));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    [RelayCommand]
    private void ManageProfiles() => ManageProfilesRequested?.Invoke();
}
