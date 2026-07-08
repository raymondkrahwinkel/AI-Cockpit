using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Voice;

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
    private readonly ISessionTranscriptReader? _transcriptReader;
    private ClaudeProfile? _configuredProfile;
    private string? _configuredPermissionMode;
    private string? _configuredModel;
    private string? _configuredEffort;
    private bool _isLaunchConfigured;
    private bool _launched;

    /// <summary>
    /// Forced onto the launched CLI via <c>--session-id</c> (#35b), so the read-aloud transcript tailer
    /// knows the exact JSONL file to watch without depending on the CLI's own cwd-hash naming rule.
    /// Minted once, in <see cref="LaunchConfigured"/>.
    /// </summary>
    private Guid _sessionId;

    private CancellationTokenSource? _transcriptTailCancellation;

    /// <summary>Raised once both the launch is configured and the view is subscribed; the view supplies the terminal size and wires the returned pty.</summary>
    public event Action<IClaudeTtyLauncher, ClaudeProfile?, Guid, string?, string?, string?>? LaunchRequested;

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

    public ClaudeTtyViewModel(
        IClaudeTtyLauncher launcher,
        IVoicePushToTalkService? voicePushToTalk = null,
        IVoiceSettingsStore? voiceSettingsStore = null,
        IVoicePlaybackQueue? voicePlaybackQueue = null,
        ISessionTranscriptReader? transcriptReader = null)
    {
        _launcher = launcher;
        _transcriptReader = transcriptReader;
        InitializeVoice(voicePushToTalk, voiceSettingsStore, voicePlaybackQueue);
    }

    /// <summary>Raw bytes, no cleanup — the terminal has no input box to proofread in, so the transcript goes straight to the pty like a typed keystroke.</summary>
    protected override void OnVoiceTextReady(string text) => VoiceTranscriptReady?.Invoke(text);

    /// <summary>Auto-submit: writes a carriage return into the pty, the same byte a physical Enter sends after typing — submits the just-injected transcript to the interactive claude TUI.</summary>
    protected override void OnVoiceSubmitRequested() => VoiceTranscriptReady?.Invoke("\r");

    /// <summary>
    /// Configures the panel with the profile and start defaults chosen in the New-session dialog, then
    /// launches the TUI as soon as the view is ready (#31). Replaces the old in-panel Start button and
    /// inline profile picker. <paramref name="permissionMode"/>/<paramref name="model"/>/
    /// <paramref name="effort"/> are launch-only: the real TUI owns any live switching afterwards.
    /// </summary>
    public void LaunchConfigured(ClaudeProfile? profile, string? permissionMode, string? model, string? effort)
    {
        _configuredProfile = profile;
        _sessionId = Guid.NewGuid();
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
        LaunchRequested.Invoke(_launcher, _configuredProfile, _sessionId, _configuredPermissionMode, _configuredModel, _configuredEffort);
    }

    /// <summary>
    /// Starts/stops tailing the live session transcript for read-aloud (#35b) as the toggle flips.
    /// Requires a configured profile (its <c>ConfigDir</c> locates the transcript) and a wired reader;
    /// both are always present on the real launch path — only the design-time/parameterless VM lacks
    /// them, where the toggle simply has nothing to tail.
    /// </summary>
    protected override void OnReadAloudToggleChanged(bool isEnabled)
    {
        if (!isEnabled)
        {
            _transcriptTailCancellation?.Cancel();
            _transcriptTailCancellation?.Dispose();
            _transcriptTailCancellation = null;
            return;
        }

        if (_transcriptReader is null || _configuredProfile is null || _transcriptTailCancellation is not null)
        {
            return;
        }

        _transcriptTailCancellation = new CancellationTokenSource();
        _ = _TailTranscriptForReadAloudAsync(_configuredProfile.ConfigDir, _sessionId, _transcriptTailCancellation.Token);
    }

    /// <summary>Consumes the transcript tailer and enqueues each assistant turn's prose for TTS — mirrors <c>ClaudeSessionViewModel._EnqueueTurnProseForReadAloud</c>, just fed by the tailer instead of the SDK event stream.</summary>
    private async Task _TailTranscriptForReadAloudAsync(string configDir, Guid sessionId, CancellationToken cancellationToken)
    {
        if (_transcriptReader is null)
        {
            return;
        }

        try
        {
            await foreach (var assistantText in _transcriptReader.ReadAssistantTextAsync(configDir, sessionId, cancellationToken))
            {
                EnqueueReadAloud(TtsProseExtractor.Extract(assistantText), TtsVoiceId);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the toggle is switched off or the panel closes.
        }
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

    /// <summary>Called by the view once the pty has actually spawned, so the header stops reading "Launching TUI..." while the real TUI is already interactive below it.</summary>
    public void OnLaunchSucceeded()
    {
        Status = "Running";
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
        // the ConPtyProcess on unload/close. The transcript tailer is this VM's own background loop,
        // so it does need stopping here — otherwise it would keep polling a file for a session that no
        // longer has a panel to read aloud into.
        _transcriptTailCancellation?.Cancel();
        _transcriptTailCancellation?.Dispose();
        _transcriptTailCancellation = null;
        return ValueTask.CompletedTask;
    }
}
