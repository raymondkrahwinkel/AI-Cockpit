using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

// AC-1013: routes the desktop-wide push-to-talk key (#34) to the selected session and floating overlay — a hold
// starts capture, and on release the session's STT reports the pill (AC-557); global arming is
// `GlobalHotkeyCoordinator`'s. Hotkey events fire off the UI thread, so the hold handlers marshal onto it.
public sealed class VoicePushToTalkCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly CockpitViewModel _cockpit;
    private readonly VoiceOverlayCoordinator _overlayCoordinator;
    private readonly IVoicePushToTalkService _pushToTalk;
    private readonly ILogger<VoicePushToTalkCoordinator> _logger;

    // Whether the hold in progress actually opened a microphone — see `HandleHoldStarted`.
    private bool _isRecording;

    public VoicePushToTalkCoordinator(
        GlobalHotkeyCoordinator hotkeys,
        CockpitViewModel cockpit,
        VoiceOverlayCoordinator overlayCoordinator,
        IVoicePushToTalkService pushToTalk,
        ILogger<VoicePushToTalkCoordinator> logger)
    {
        _hotkeys = hotkeys;
        _cockpit = cockpit;
        _overlayCoordinator = overlayCoordinator;
        _pushToTalk = pushToTalk;
        _logger = logger;

        // Subscribed once, for the life of the app: the hotkey coordinator re-arms in place rather than handing
        // out a new event source, so there is no re-subscription here to accidentally do twice and double every
        // hold. Both events carry the id of the key that fired, and this one only wants push-to-talk's.
        _hotkeys.Pressed += (_, id) => { if (id == GlobalHotkeys.PushToTalk) { _OnHoldStarted(); } };
        _hotkeys.Released += (_, id) => { if (id == GlobalHotkeys.PushToTalk) { _OnHoldEnded(); } };

        // The key and the on/off flag are read when the hotkey is armed, which happened once at startup. Saving
        // them has to re-arm, or the setting is a field that remembers what you typed and changes nothing.
        _cockpit.VoiceSettingsSaved += (_, _) => _ = _hotkeys.ApplyAsync();

        // AC-691: the operator's on-demand ask for a fresh portal permission prompt. Same re-arm as above — on
        // Wayland it is what makes PortalGlobalHotkeyService.StartAsync tear down and rebuild its session.
        _cockpit.HotkeyPortalRetryRequested += (_, _) => _ = _hotkeys.ApplyAsync();

        // The compositor may rebind this from its own shortcut settings at any time, without the cockpit being
        // asked. Following it is the difference between reporting the trigger and reporting a guess.
        _hotkeys.TriggerDescriptionsChanged += (_, _) => Dispatcher.UIThread.Post(HandleTriggerDescriptionsChanged);
    }

    // Test seam, like the hold handlers below: puts the trigger where the operator can see it — or says why there is none. What the cases are is `GlobalHotkeyCoordinator.DescribeTrigger`'s; the words for this key are here.
    internal void HandleTriggerDescriptionsChanged() =>
        _cockpit.VoiceGlobalHotkeyTrigger = _hotkeys.DescribeTrigger(
            GlobalHotkeys.PushToTalk,
            unboundMessage: "Your desktop has not bound it yet. Look for “Push to talk (hold)” in its own shortcut settings.",
            unsupportedMessage: "Not available on macOS — the in-window key still works while the cockpit has focus.",
            failedMessage: "It is switched on but could not be registered — see the log. The in-window key still works while the cockpit has focus.");

    // The pill's view model. Reports what the hold is doing; what the pill actually shows is `VoiceOverlayCoordinator`'s call, since open-mic and read-aloud want it too.
    public VoiceOverlayViewModel Overlay => _overlayCoordinator.Overlay;

    private void _OnHoldStarted() => Dispatcher.UIThread.Post(HandleHoldStarted);

    private void _OnHoldEnded() => Dispatcher.UIThread.Post(() => _ = HandleHoldEndedAsync());

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => _overlayCoordinator.PushLevel(level));

    // Test seam: the UI-thread logic for a hold starting — see the threading remarks on this class.
    internal void HandleHoldStarted()
    {
        // AC-627: no stand-down for open-mic any more — it steps aside in
        // `SessionPanelViewModel.BeginVoiceHold`, where this route and the in-window one meet.

        // Detached first so this cannot stack, whatever the backend does with a repeated key. Today neither of
        // them repeats a hold, so the -= finds nothing — but that is a promise another class makes, and the one
        // subscription per hold this needs should not depend on it being kept.
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;
        _pushToTalk.AudioLevelSampled += _OnAudioLevelSampled;

        var session = _cockpit.SelectedSession;
        var capturing = session?.BeginVoiceHold() ?? false;

        // Resolved before the pill is shown, not after: it used to flip to "Listening" unconditionally, leaving an
        // operator holding the key over an empty cockpit watching a flat waveform with no idea why nothing came out.
        var blocked = capturing ? null : _WhyNothingIsBeingRecorded(session);
        _isRecording = blocked is null;

        // Only what this coordinator alone knows. A hold that did start says "Listening" from the session itself
        // (`SessionPanelViewModel.BeginVoiceHold`), which is also the in-window F9 route's only way to
        // say it — and saying it in both places would show the pill twice for one hold.
        if (blocked is not null)
        {
            _overlayCoordinator.SetPushToTalk(VoiceOverlayState.Unavailable, blocked);
        }

        // AC-603: the press is the promise that a transcription is coming, and the sentence spoken into it is the
        // only window in which the model can load for free. Not awaited, and a failure is first use's to report.
        if (_isRecording)
        {
            _ = _pushToTalk.WarmUpAsync();
        }

        // Kept: which session the hold routed to, and whether capture truly began, is still what tells a wrong
        // routing apart from a declined hold when a dictation later yields nothing.
        _logger.LogInformation(
            "Push-to-talk hold started: session='{Session}' voiceEnabled={VoiceEnabled} capturing={Capturing} sessions={SessionCount}",
            session?.Title ?? "<none selected>",
            session?.VoiceEnabled,
            capturing,
            _cockpit.Sessions.Count);
    }

    // AC-1013: why a hold is not recording, in words for the pill, or null. A declined hold with no reason here
    // means `PushToTalkHoldGuard` already has one running and that pill must be left alone — it is NOT OS key-repeat
    // (both hotkey backends collapse a hold to a single edge, so repeat cannot reach this coordinator).
    private static string? _WhyNothingIsBeingRecorded(SessionPanelViewModel? session) => session switch
    {
        null => "No session selected",
        { VoiceEnabled: false } => "Voice is off for this session",
        _ => null,
    };

    // Test seam: the UI-thread logic for a hold ending — see the threading remarks on this class.
    internal async Task HandleHoldEndedAsync()
    {
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;

        // Nothing was captured, so there is nothing to transcribe. Flashing "Transcribing…" over an empty
        // recording would be the same lie in a different word — and the reason the pill is showing is the one
        // thing worth leaving on screen for the moment the key is still down.
        if (!_isRecording)
        {
            _overlayCoordinator.SetPushToTalk(null);

            return;
        }

        var session = _cockpit.SelectedSession;
        if (session is null)
        {
            // Nothing to report the release to — the session went away while the key was held.
            _overlayCoordinator.SetPushToTalk(null);
            return;
        }

        // AC-557: what the hold is doing from here is the session's own report (`EndVoiceHoldAsync`), because the
        // in-window F9 handlers end their hold through that same method. The pill is deliberately not taken down
        // here any more — it used to be, unconditionally, hiding a failed/empty dictation's explanation instantly.
        await session.EndVoiceHoldAsync();
    }
}
