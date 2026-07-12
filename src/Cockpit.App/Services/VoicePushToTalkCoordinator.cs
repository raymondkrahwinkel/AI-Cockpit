using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;

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

    public VoicePushToTalkCoordinator(
        IGlobalHotkeyService hotkeyService,
        CockpitViewModel cockpit,
        IVoiceSettingsStore voiceSettingsStore,
        VoiceOverlayViewModel overlay,
        IVoiceOverlayPresenter overlayPresenter,
        IVoicePushToTalkService pushToTalk)
    {
        _hotkeyService = hotkeyService;
        _cockpit = cockpit;
        _voiceSettingsStore = voiceSettingsStore;
        _overlayPresenter = overlayPresenter;
        _pushToTalk = pushToTalk;
        Overlay = overlay;
    }

    public VoiceOverlayViewModel Overlay { get; }

    /// <summary>Starts listening for the global hotkey. No-op when voice or global push-to-talk is off, so the portal/hook is never touched for an operator who never opted in.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _voiceSettingsStore.LoadAsync(cancellationToken);
        if (!settings.IsEnabled || !settings.GlobalPushToTalk)
        {
            return;
        }

        _hotkeyService.HoldStarted += _OnHoldStarted;
        _hotkeyService.HoldEnded += _OnHoldEnded;
        await _hotkeyService.StartAsync(cancellationToken);
    }

    private void _OnHoldStarted(object? sender, EventArgs e) => Dispatcher.UIThread.Post(HandleHoldStarted);

    private void _OnHoldEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = HandleHoldEndedAsync());

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => Overlay.PushLevel(level));

    /// <summary>Test seam: the UI-thread logic for a hold starting — see the threading remarks on this class.</summary>
    internal void HandleHoldStarted()
    {
        Overlay.State = VoiceOverlayState.Listening;
        _overlayPresenter.Show();
        _pushToTalk.AudioLevelSampled += _OnAudioLevelSampled;
        _cockpit.SelectedSession?.BeginVoiceHold();
    }

    /// <summary>Test seam: the UI-thread logic for a hold ending — see the threading remarks on this class.</summary>
    internal async Task HandleHoldEndedAsync()
    {
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;
        Overlay.State = VoiceOverlayState.Transcribing;

        var session = _cockpit.SelectedSession;
        if (session is not null)
        {
            // SDK sessions get the Ollama cleanup pass; TTY has none, since its transcript is written
            // as raw pty bytes — the same split SessionView/ClaudeTtyView's local F9 handlers
            // already make.
            await session.EndVoiceHoldAsync(applyCleanup: session is not ClaudeTtyViewModel);
        }

        Overlay.State = VoiceOverlayState.Hidden;
        _overlayPresenter.Hide();
    }
}
