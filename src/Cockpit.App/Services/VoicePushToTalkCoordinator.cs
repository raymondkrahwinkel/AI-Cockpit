using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Routes the desktop-wide push-to-talk key to the currently selected session and the floating voice
/// overlay (#34): a hold starts, the overlay shows "Listening" and the selected session's microphone capture
/// begins; the hold ends, the overlay flips to "Transcribing", the session's own STT+cleanup pipeline runs,
/// and the overlay hides once the text has been injected.
/// </summary>
/// <remarks>
/// Whether the key is armed at all is <see cref="GlobalHotkeyCoordinator"/>'s: it registers only what the
/// operator switched on, so with global push-to-talk off nothing arrives here and the per-view local F9
/// handlers keep doing the job untouched.
/// <para>
/// Threading: <see cref="GlobalHotkeyCoordinator.Pressed"/>/<see cref="GlobalHotkeyCoordinator.Released"/>
/// fire on the backend's own thread (the D-Bus loop on Linux, the keyboard-hook thread on Windows),
/// never the UI thread — every touch of <see cref="CockpitViewModel"/> or the overlay is marshaled onto
/// the UI thread via <see cref="Dispatcher.UIThread"/> first. <see cref="HandleHoldStarted"/> and
/// <see cref="HandleHoldEndedAsync"/> are the actual (UI-thread) routing logic and the seam the tests
/// drive directly, since pumping a real Avalonia dispatcher loop from a unit test is not practical.
/// </para>
/// </remarks>
public sealed class VoicePushToTalkCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly CockpitViewModel _cockpit;
    private readonly VoiceOverlayCoordinator _overlayCoordinator;
    private readonly IVoicePushToTalkService _pushToTalk;
    private readonly IOpenMicState? _openMicState;
    private readonly ILogger<VoicePushToTalkCoordinator> _logger;

    /// <summary>Whether the hold in progress actually opened a microphone — see <see cref="HandleHoldStarted"/>.</summary>
    private bool _isRecording;

    public VoicePushToTalkCoordinator(
        GlobalHotkeyCoordinator hotkeys,
        CockpitViewModel cockpit,
        VoiceOverlayCoordinator overlayCoordinator,
        IVoicePushToTalkService pushToTalk,
        ILogger<VoicePushToTalkCoordinator> logger,
        IOpenMicState? openMicState = null)
    {
        _hotkeys = hotkeys;
        _cockpit = cockpit;
        _overlayCoordinator = overlayCoordinator;
        _pushToTalk = pushToTalk;
        _openMicState = openMicState;
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

    /// <summary>Test seam, like the hold handlers below: puts the trigger where the operator can see it — or says why there is none. What the cases are is <see cref="GlobalHotkeyCoordinator.DescribeTrigger"/>'s; the words for this key are here.</summary>
    internal void HandleTriggerDescriptionsChanged() =>
        _cockpit.VoiceGlobalHotkeyTrigger = _hotkeys.DescribeTrigger(
            GlobalHotkeys.PushToTalk,
            unboundMessage: "Your desktop has not bound it yet. Look for “Push to talk (hold)” in its own shortcut settings.",
            unsupportedMessage: "Not available on macOS — the in-window key still works while the cockpit has focus.");

    /// <summary>The pill's view model. Reports what the hold is doing; what the pill actually shows is <see cref="VoiceOverlayCoordinator"/>'s call, since open-mic and read-aloud want it too.</summary>
    public VoiceOverlayViewModel Overlay => _overlayCoordinator.Overlay;

    private void _OnHoldStarted() => Dispatcher.UIThread.Post(HandleHoldStarted);

    private void _OnHoldEnded() => Dispatcher.UIThread.Post(() => _ = HandleHoldEndedAsync());

    private void _OnAudioLevelSampled(object? sender, double level) => Dispatcher.UIThread.Post(() => _overlayCoordinator.PushLevel(level));

    /// <summary>Test seam: the UI-thread logic for a hold starting — see the threading remarks on this class.</summary>
    internal void HandleHoldStarted()
    {
        // Open-mic is already capturing and transcribing continuously; routing a hold to the session on top of it
        // would land the same speech twice. Open-mic wins — say why and start no hold. The local per-view path
        // stands down the same way (see PushToTalkKeyGate).
        if (_openMicState?.IsListening == true)
        {
            _isRecording = false;
            _overlayCoordinator.SetPushToTalk(VoiceOverlayState.Unavailable, "Open mic is on");
            return;
        }

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

        _overlayCoordinator.SetPushToTalk(
            blocked is null ? VoiceOverlayState.Listening : VoiceOverlayState.Unavailable,
            blocked);

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
    /// Why a hold is not recording, in words for the pill — or null when there is nothing to explain. A declined
    /// hold with no reason here means <see cref="PushToTalkHoldGuard"/> still has one running: that pill is
    /// already listening and must be left alone.
    /// </summary>
    /// <remarks>
    /// It is <em>not</em> the OS repeating the held key, which this used to say. Key-repeat is real on the local
    /// per-view F9 handlers — Avalonia raises KeyDown for every repeat, which is what the hold guard was written
    /// for — but it cannot reach this coordinator: both hotkey backends collapse a hold to a single edge
    /// (<c>SharpHookGlobalHotkeyService</c> and <c>PortalGlobalHotkeyService</c> each gate their
    /// <c>HoldStarted</c> on an <c>_isHolding</c> flag), and the local handlers stand down entirely while global
    /// push-to-talk is on (<see cref="PushToTalkKeyGate"/>). The claim came from the local path and was carried
    /// here, where it is not true — and it later cost a code review a finding chased against a comment rather
    /// than the code.
    /// </remarks>
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
            _overlayCoordinator.SetPushToTalk(null);

            return;
        }

        _overlayCoordinator.SetPushToTalk(VoiceOverlayState.Transcribing);

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
                // as raw pty bytes — the same split SessionView/TtyView's local F9 handlers
                // already make.
                await session.EndVoiceHoldAsync(applyCleanup: session is not TtyViewModel);
            }
        }
        finally
        {
            _pushToTalk.Preparing -= _OnPreparing;
            _pushToTalk.Prepared -= _OnPrepared;
        }

        // The hold has nothing left to say. Whether the pill goes away is not this coordinator's call: read-aloud
        // may have started while the transcript was being produced, and it gets the pill once dictation is done.
        _overlayCoordinator.SetPushToTalk(null);
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
    internal void HandlePreparing(VoicePreparationProgress step) =>
        _overlayCoordinator.SetPushToTalk(VoiceOverlayState.Preparing, step.Description, step.Fraction);

    /// <summary>Test seam: preparation is over, so the pill goes back to the plain spinner — which is now telling the truth.</summary>
    internal void HandlePrepared() => _overlayCoordinator.SetPushToTalk(VoiceOverlayState.Transcribing);
}
