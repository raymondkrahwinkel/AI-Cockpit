using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Voice;

namespace Cockpit.App.Services;

// Routes the desktop-wide push-to-talk key to the currently selected session and the floating voice
// overlay (#34): a hold starts, the overlay shows "Listening" and the selected session's microphone capture
// begins; the hold ends, the session's own STT pipeline runs, and the overlay hides once the text has been
// injected. What the pill says from the release onwards — the spinner, a first-use download, or why the
// dictation produced nothing — is the session's to report, since the in-window F9 handlers end their hold
// through that same method and would otherwise have nothing to say at all (AC-557).
// Whether the key is armed at all is `GlobalHotkeyCoordinator`'s: it registers only what the
// operator switched on, so with global push-to-talk off nothing arrives here and the per-view local F9
// handlers keep doing the job untouched.
//
// Threading: `GlobalHotkeyCoordinator.Pressed`/`GlobalHotkeyCoordinator.Released`
// fire on the backend's own thread (the D-Bus loop on Linux, the keyboard-hook thread on Windows),
// never the UI thread — every touch of `CockpitViewModel` or the overlay is marshaled onto
// the UI thread via `Dispatcher.UIThread` first. `HandleHoldStarted` and
// `HandleHoldEndedAsync` are the actual (UI-thread) routing logic and the seam the tests
// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
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

        // Resolved before the pill is shown, not after. It used to flip to "Listening" unconditionally and this
        // very comment admitted that seeing it "says nothing about whether the microphone actually opened" —
        // and then wrote the truth to the log. An operator holding the key over an empty cockpit watched a flat
        // waveform and had no way to know why nothing came out.
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

    // Why a hold is not recording, in words for the pill — or null when there is nothing to explain. A declined
    // hold with no reason here means `PushToTalkHoldGuard` still has one running: that pill is
    // already listening and must be left alone.
    // It is *not* the OS repeating the held key, which this used to say. Key-repeat is real on the local
    // per-view F9 handlers — Avalonia raises KeyDown for every repeat, which is what the hold guard was written
    // for — but it cannot reach this coordinator: both hotkey backends collapse a hold to a single edge
    // (`SharpHookGlobalHotkeyService` and `PortalGlobalHotkeyService` each gate their
    // `HoldStarted` on an `_isHolding` flag), and the local handlers stand down entirely while global
    // push-to-talk is on (`PushToTalkKeyGate`). The claim came from the local path and was carried
    // here, where it is not true — and it later cost a code review a finding chased against a comment rather
    // than the code.
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

        // What the hold is doing from here — the spinner, a first-use download, and what it produced — is the
        // session's own report (`SessionPanelViewModel.EndVoiceHoldAsync`), because the in-window F9
        // handlers end their hold through that same method and had no way to say any of it.
        //
        // Which is also why the pill is not taken down here any more. It was, unconditionally, and that is the
        // defect: a dictation that failed or heard no speech had its explanation hidden the instant it appeared,
        // leaving an operator who had just talked for a minute with nothing at all (AC-557). The session hides it
        // on a transcript and leaves its own reason standing when there was none; that message clears itself.
        await session.EndVoiceHoldAsync();
    }
}
