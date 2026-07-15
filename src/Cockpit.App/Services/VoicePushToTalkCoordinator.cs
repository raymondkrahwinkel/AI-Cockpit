using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Wires the OS-specific <see cref="IGlobalHotkeyService"/> to the currently selected session and the
/// floating voice overlay (#34): a hold starts, the overlay shows "Listening" and the selected
/// session's microphone capture begins; the hold ends, the overlay flips to "Transcribing", the
/// session's own STT+cleanup pipeline runs, and the overlay hides once the text has been injected. Only
/// starts listening when both voice and <see cref="Cockpit.Core.Voice.VoiceSettings.GlobalPushToTalk"/>
/// are enabled — otherwise the existing per-view local F9 handlers keep doing the job untouched.
/// </summary>
/// <remarks>
/// Threading: <see cref="IGlobalHotkeyService.HoldStarted"/>/<see cref="IGlobalHotkeyService.HoldEnded"/>
/// fire on the backend's own thread (the D-Bus loop on Linux, the keyboard-hook thread on Windows),
/// never the UI thread — every touch of <see cref="CockpitViewModel"/> or the overlay is marshaled onto
/// the UI thread via <see cref="Dispatcher.UIThread"/> first. <see cref="HandleHoldStarted"/> and
/// <see cref="HandleHoldEndedAsync"/> are the actual (UI-thread) routing logic and the seam the tests
/// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
/// </remarks>
public sealed class VoicePushToTalkCoordinator : ISingletonService
{
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly CockpitViewModel _cockpit;
    private readonly IVoiceSettingsStore _voiceSettingsStore;
    private readonly IVoiceOverlayPresenter _overlayPresenter;
    private readonly IVoicePushToTalkService _pushToTalk;
    private readonly ILogger<VoicePushToTalkCoordinator> _logger;

    /// <summary>Whether the hold in progress actually opened a microphone — see <see cref="HandleHoldStarted"/>.</summary>
    private bool _isRecording;

    public VoicePushToTalkCoordinator(
        IGlobalHotkeyService hotkeyService,
        CockpitViewModel cockpit,
        IVoiceSettingsStore voiceSettingsStore,
        VoiceOverlayViewModel overlay,
        IVoiceOverlayPresenter overlayPresenter,
        IVoicePushToTalkService pushToTalk,
        ILogger<VoicePushToTalkCoordinator> logger)
    {
        _hotkeyService = hotkeyService;
        _cockpit = cockpit;
        _voiceSettingsStore = voiceSettingsStore;
        _overlayPresenter = overlayPresenter;
        _pushToTalk = pushToTalk;
        _logger = logger;
        Overlay = overlay;
    }

    public VoiceOverlayViewModel Overlay { get; }

    /// <summary>Starts listening for the global hotkey. No-op when voice or global push-to-talk is off, so the portal/hook is never touched for an operator who never opted in.</summary>
    /// <remarks>
    /// Never throws. Its one caller discards the task (<c>App.axaml.cs</c>: <c>_ = …StartAsync()</c>), so anything
    /// thrown here used to land on a task nobody observes and be gone — and what it took with it was the hotkey.
    /// Reading the voice settings goes through the shared <c>cockpit.json</c>, which a write elsewhere in this
    /// process can briefly lock; on 2026-07-15 that raced at startup and F9 was dead for the whole session with
    /// not one line in the log to say so. It still cannot start if the read fails — but now it says which.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
            if (!settings.IsEnabled || !settings.GlobalPushToTalk)
            {
                return;
            }

            _hotkeyService.HoldStarted += _OnHoldStarted;
            _hotkeyService.HoldEnded += _OnHoldEnded;
            await _hotkeyService.StartAsync(cancellationToken);

            _logger.LogInformation("Global push-to-talk armed on '{Key}'.", settings.PushToTalkKeyName);
        }
        catch (Exception exception)
        {
            // Leave nothing subscribed to a hook that never armed.
            _hotkeyService.HoldStarted -= _OnHoldStarted;
            _hotkeyService.HoldEnded -= _OnHoldEnded;

            _logger.LogError(
                exception,
                "Global push-to-talk could not start; the hotkey will not fire until the cockpit is restarted.");
        }
    }

    private void _OnHoldStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleHoldStarted);

    private void _OnHoldEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = HandleHoldEndedAsync());

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => Overlay.PushLevel(level));

    /// <summary>Test seam: the UI-thread logic for a hold starting — see the threading remarks on this class.</summary>
    internal void HandleHoldStarted()
    {
        _pushToTalk.AudioLevelSampled += _OnAudioLevelSampled;
        var session = _cockpit.SelectedSession;
        var capturing = session?.BeginVoiceHold() ?? false;

        // Resolved before the pill is shown, not after. It used to flip to "Listening" unconditionally and this
        // very comment admitted that seeing it "says nothing about whether the microphone actually opened" —
        // and then wrote the truth to the log. An operator holding the key over an empty cockpit watched a flat
        // waveform and had no way to know why nothing came out.
        var blocked = capturing ? null : _WhyNothingIsBeingRecorded(session);
        _isRecording = blocked is null;

        if (blocked is null)
        {
            Overlay.State = VoiceOverlayState.Listening;
        }
        else
        {
            Overlay.StatusText = blocked;
            Overlay.State = VoiceOverlayState.Unavailable;
        }

        _overlayPresenter.Show();

        // Kept: which session the hold routed to, and whether capture truly began, is still what tells a wrong
        // routing apart from a declined hold when a dictation later yields nothing.
        _logger.LogInformation(
            "Push-to-talk hold started: session='{Session}' voiceEnabled={VoiceEnabled} capturing={Capturing} sessions={SessionCount}",
            session?.Title ?? "<none selected>",
            session?.VoiceEnabled,
            capturing,
            _cockpit.Sessions.Count);
    }

    /// <summary>
    /// Why a hold is not recording, in words for the pill — or null when there is nothing to explain. A
    /// declined hold with no reason here means one is already running (the OS repeating the held key), which is
    /// the guard doing its job: that pill is already listening and must be left alone.
    /// </summary>
    private static string? _WhyNothingIsBeingRecorded(SessionPanelViewModel? session) => session switch
    {
        null => "No session selected",
        { VoiceEnabled: false } => "Voice is off for this session",
        _ => null,
    };

    /// <summary>Test seam: the UI-thread logic for a hold ending — see the threading remarks on this class.</summary>
    internal async Task HandleHoldEndedAsync()
    {
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;

        // Nothing was captured, so there is nothing to transcribe. Flashing "Transcribing…" over an empty
        // recording would be the same lie in a different word — and the reason the pill is showing is the one
        // thing worth leaving on screen for the moment the key is still down.
        if (!_isRecording)
        {
            Overlay.State = VoiceOverlayState.Hidden;
            _overlayPresenter.Hide();

            return;
        }

        Overlay.State = VoiceOverlayState.Transcribing;

        // Only for as long as this hold: first use fetches gigabytes before it can transcribe, and the pill
        // spent that time on a spinner that said "Transcribing…". Subscribed here rather than for the
        // coordinator's lifetime so a step can never repaint the pill after its hold is over.
        _pushToTalk.Preparing += _OnPreparing;
        _pushToTalk.Prepared += _OnPrepared;

        try
        {
            var session = _cockpit.SelectedSession;
            if (session is not null)
            {
                // SDK sessions get the Ollama cleanup pass; TTY has none, since its transcript is written
                // as raw pty bytes — the same split SessionView/ClaudeTtyView's local F9 handlers
                // already make.
                await session.EndVoiceHoldAsync(applyCleanup: session is not ClaudeTtyViewModel);
            }
        }
        finally
        {
            _pushToTalk.Preparing -= _OnPreparing;
            _pushToTalk.Prepared -= _OnPrepared;
        }

        Overlay.State = VoiceOverlayState.Hidden;
        _overlayPresenter.Hide();
    }

    /// <summary>
    /// Fires off the UI thread (the download's own), so it marshals like the level feed does. Each step both
    /// puts the pill into <see cref="VoiceOverlayState.Preparing"/> and names what it is waiting on — the
    /// state cannot be set up front, because on every run after the first there is nothing to prepare and the
    /// pill should go straight to transcribing.
    /// </summary>
    private void _OnPreparing(object? sender, VoicePreparationProgress step) =>
        Dispatcher.UIThread.Post(() => HandlePreparing(step));

    private void _OnPrepared(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(HandlePrepared);

    /// <summary>
    /// Test seam, like the hold handlers above: the UI-thread half of a preparation step. Each step sets the
    /// state as well as the text — it cannot be set up front, because on every run after the first there is
    /// nothing to prepare and the pill should go straight to its spinner.
    /// </summary>
    internal void HandlePreparing(VoicePreparationProgress step)
    {
        Overlay.StatusText = step.Description;
        Overlay.Progress = step.Fraction;
        Overlay.State = VoiceOverlayState.Preparing;
    }

    /// <summary>Test seam: preparation is over, so the pill goes back to the plain spinner — which is now telling the truth.</summary>
    internal void HandlePrepared() => Overlay.State = VoiceOverlayState.Transcribing;
}
