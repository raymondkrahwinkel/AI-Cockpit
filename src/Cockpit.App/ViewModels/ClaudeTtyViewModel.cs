using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// TTY-mode (#9) session panel: hosts the real interactive <c>claude</c> TUI inside a ConPTY,
/// rendered by <c>ClaudeTtyView</c>'s terminal control. This view model owns the profile choice and
/// status; the view owns the terminal size, so when a profile is resolved the VM raises
/// <see cref="LaunchRequested"/> and the view calls the carried <see cref="IClaudeTtyLauncher"/> with
/// its current columns/rows.
/// </summary>
/// <remarks>
/// Registered <c>ITransientService</c> so <c>CockpitViewModel</c>'s factory mints one per TTY session.
/// Windows-first: the underlying ConPTY host is Windows-only in this PoC.
/// </remarks>
public partial class ClaudeTtyViewModel : SessionPanelViewModel, ITransientService
{
    private readonly IClaudeTtyLauncher? _launcher;
    private readonly IClaudeProfileStore? _profileStore;
    private readonly IClaudeProfileLoginChecker? _loginChecker;

    /// <summary>Raised once a profile is resolved; the view supplies the terminal size and wires the returned pty.</summary>
    public event Action<IClaudeTtyLauncher, ClaudeProfile?>? LaunchRequested;

    /// <summary>Populated only while a profile choice is pending the user's pick.</summary>
    public ObservableCollection<ClaudeProfile> ProfileChoices { get; } = [];

    [ObservableProperty]
    private bool _isChoosingProfile;

    [ObservableProperty]
    private ClaudeProfile? _selectedProfile;

    [ObservableProperty]
    private string _status = "Not started.";

    /// <summary>True once the TUI has been launched, so the Start button hides.</summary>
    [ObservableProperty]
    private bool _isLaunched;

    // Parameterless constructor for the Avalonia previewer/Screenshotter design-time context.
    public ClaudeTtyViewModel()
    {
        ActiveProfileLabel = "werk";
        Status = "TTY mode (experiment).";
    }

    public ClaudeTtyViewModel(IClaudeTtyLauncher launcher, IClaudeProfileStore profileStore, IClaudeProfileLoginChecker loginChecker)
    {
        _launcher = launcher;
        _profileStore = profileStore;
        _loginChecker = loginChecker;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_launcher is null || _profileStore is null || _loginChecker is null || IsLaunched)
        {
            return;
        }

        Status = "Checking profiles...";

        var profiles = await _profileStore.LoadAsync();
        var statuses = profiles.Select(p => new ClaudeProfileStatus(p, _loginChecker.IsLoggedIn(p))).ToList();
        var outcome = ProfileSelector.Select(statuses);

        switch (outcome.Kind)
        {
            case ProfileSelectionKind.LoginRequired:
                Status = "No logged-in Claude profile found. Run 'claude /login' in a terminal, then try again.";
                return;

            case ProfileSelectionKind.RequiresChoice:
                ProfileChoices.Clear();
                foreach (var candidate in outcome.Candidates)
                {
                    ProfileChoices.Add(candidate);
                }

                SelectedProfile = outcome.Candidates[0];
                IsChoosingProfile = true;
                Status = "Choose a profile to start the TUI.";
                return;

            case ProfileSelectionKind.UseSilently:
                LaunchWithProfile(outcome.SingleProfile);
                return;
        }
    }

    [RelayCommand]
    private void ConfirmProfileChoice()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        IsChoosingProfile = false;
        LaunchWithProfile(SelectedProfile);
    }

    private void LaunchWithProfile(ClaudeProfile? profile)
    {
        if (_launcher is null)
        {
            return;
        }

        ActiveProfileLabel = profile?.Label;
        Status = profile is null ? "Launching TUI..." : $"Launching TUI ({profile.Label})...";
        IsLaunched = true;
        SessionStatus = SessionStatus.Busy;
        LaunchRequested?.Invoke(_launcher, profile);
    }

    /// <summary>Called by the view when the hosted process exits, to reflect it in the sidebar status.</summary>
    public void OnProcessExited()
    {
        Status = "TUI process exited.";
        SessionStatus = SessionStatus.Done;
    }

    public override ValueTask DisposeAsync()
    {
        // The terminal control owns the pty lifetime (it created it via the launcher); it disposes
        // the ConPtyProcess on unload/close. Nothing session-scoped to tear down here.
        return ValueTask.CompletedTask;
    }
}
