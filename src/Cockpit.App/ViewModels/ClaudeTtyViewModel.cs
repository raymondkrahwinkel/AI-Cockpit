using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// TTY-mode (#9) session panel: hosts the real interactive <c>claude</c> TUI inside a ConPTY, rendered
/// by <c>ClaudeTtyView</c>'s terminal control. The profile and start defaults (permission mode/model/
/// effort) are chosen up front in the New-session dialog (#31) and handed in via
/// <see cref="LaunchConfigured"/>; the view owns the terminal size, so the VM raises
/// <see cref="LaunchRequested"/> and the view launches the carried <see cref="IClaudeTtyLauncher"/>
/// with its current columns/rows once it has a size.
/// </summary>
/// <remarks>
/// Registered <c>ITransientService</c> so <c>CockpitViewModel</c>'s factory mints one per TTY session.
/// The underlying pty host is cross-platform (ConPTY on Windows, Porta.Pty on Linux/macOS), selected
/// by <c>IPtyHostFactory</c>.
/// </remarks>
public partial class ClaudeTtyViewModel : SessionPanelViewModel, ITransientService
{
    private readonly IClaudeTtyLauncher? _launcher;
    private ClaudeProfile? _configuredProfile;
    private string? _configuredPermissionMode;
    private string? _configuredModel;
    private string? _configuredEffort;
    private bool _isLaunchConfigured;
    private bool _launched;

    /// <summary>Raised once both the launch is configured and the view is subscribed; the view supplies the terminal size and wires the returned pty.</summary>
    public event Action<IClaudeTtyLauncher, ClaudeProfile?, string?, string?, string?>? LaunchRequested;

    /// <summary>
    /// Raised once a push-to-talk hold finished transcribing (no cleanup applied — TTY is a raw
    /// keystroke stream, so a cleaned-up transcript with different wording would be actively wrong).
    /// The view writes the text as raw bytes to the pty's stdin, the same path as a typed keystroke.
    /// </summary>
    public event Action<string>? VoiceTranscriptReady;

    [ObservableProperty]
    private string _status = "Not started.";

    // Parameterless constructor for the Avalonia previewer/Screenshotter design-time context.
    public ClaudeTtyViewModel()
    {
        ActiveProfileLabel = "work";
        Status = "TTY mode (experiment).";
    }

    public ClaudeTtyViewModel(IClaudeTtyLauncher launcher, IVoicePushToTalkService? voicePushToTalk = null, IVoiceSettingsStore? voiceSettingsStore = null)
    {
        _launcher = launcher;
        InitializeVoice(voicePushToTalk, voiceSettingsStore);
    }

    /// <summary>Raw bytes, no cleanup — the terminal has no input box to proofread in, so the transcript goes straight to the pty like a typed keystroke.</summary>
    protected override void OnVoiceTextReady(string text) => VoiceTranscriptReady?.Invoke(text);

    /// <summary>
    /// Configures the panel with the profile and start defaults chosen in the New-session dialog, then
    /// launches the TUI as soon as the view is ready (#31). Replaces the old in-panel Start button and
    /// inline profile picker. <paramref name="permissionMode"/>/<paramref name="model"/>/
    /// <paramref name="effort"/> are launch-only: the real TUI owns any live switching afterwards.
    /// </summary>
    public void LaunchConfigured(ClaudeProfile? profile, string? permissionMode, string? model, string? effort)
    {
        _configuredProfile = profile;
        _configuredPermissionMode = permissionMode;
        _configuredModel = model;
        _configuredEffort = effort;
        _isLaunchConfigured = true;
        ActiveProfileLabel = profile?.Label;
        Status = profile is null ? "Launching TUI..." : $"Launching TUI ({profile.Label})...";
        SessionStatus = SessionStatus.Busy;
        TryRaiseLaunch();
    }

    /// <summary>
    /// Raises <see cref="LaunchRequested"/> once both the profile is configured and the view is
    /// subscribed. Called from both sides — the dialog result and the view's subscription — so whichever
    /// happens second fires it; the guard makes it launch exactly once.
    /// </summary>
    public void TryRaiseLaunch()
    {
        // Only commit the launch once there is a subscriber to receive it: if the profile is configured
        // before the view exists, LaunchRequested is still null, so we must not mark it launched yet —
        // the view calls this again once subscribed.
        if (_launched || !_isLaunchConfigured || _launcher is null || LaunchRequested is null)
        {
            return;
        }

        _launched = true;
        LaunchRequested.Invoke(_launcher, _configuredProfile, _configuredPermissionMode, _configuredModel, _configuredEffort);
    }

    /// <summary>
    /// Called by the view when the hosted TUI process exits after running (the user closed claude in the
    /// TUI, or it ended). A TTY panel is a live terminal with nothing left to interact with once the
    /// process is gone, so ask the cockpit to close the panel — mirrors closing claude itself.
    /// </summary>
    public void OnProcessExited()
    {
        Status = "TUI process exited.";
        SessionStatus = SessionStatus.Done;
        RaiseCloseRequested();
    }

    /// <summary>Called by the view when the TUI could not be launched: the panel stays (the error is shown in the terminal) instead of auto-closing.</summary>
    public void OnLaunchFailed()
    {
        Status = "TUI failed to launch.";
        SessionStatus = SessionStatus.Done;
    }

    public override ValueTask DisposeAsync()
    {
        // The terminal control owns the pty lifetime (it created it via the launcher); it disposes
        // the ConPtyProcess on unload/close. Nothing session-scoped to tear down here.
        return ValueTask.CompletedTask;
    }
}
