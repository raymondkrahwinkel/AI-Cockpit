using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Voice;

namespace Cockpit.App.Services;

/// <summary>
/// Routes the assistant hotkey (F10) to the assistant instance (AC-543) — the twin of
/// <see cref="VoicePushToTalkCoordinator"/>, which routes F9 to the selected session and is left untouched.
/// </summary>
/// <remarks>
/// <b>Why not one coordinator with a flag.</b> The two paths differ in every step that matters: F9 puts text in a
/// session's composer for you to read before you send it, F10 hands it straight to the assistant. Folding them
/// together would mean a branch at each of those steps, and the failure mode of getting one branch wrong is the
/// exact thing the indicator exists to prevent — words landing in the wrong place.
/// <para>
/// <b>Straight through, no cleanup.</b> <c>EndHoldAsync(applyCleanup: false)</c>: what Whisper heard is what the
/// assistant gets (decision 10). The tidying that the removed rewrite step used to do is the system prompt's job
/// now, and a cleanup pass here would be the second copy of it — running on every utterance, for a model that
/// reads through filler words on its own.
/// </para>
/// <para>
/// Threading matches <see cref="VoicePushToTalkCoordinator"/>'s: the hotkey events arrive on the backend's own
/// thread and everything touching the view models is marshalled onto the UI thread first. The
/// <c>Handle…</c> methods are the UI-thread logic and the seam the tests drive, since pumping a real Avalonia
/// dispatcher loop from a unit test is not practical.
/// </para>
/// </remarks>
public sealed class AssistantPushToTalkCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly IAssistantSessionHost _assistant;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly IVoicePushToTalkService _pushToTalk;
    private readonly IOpenMicState? _openMicState;
    private readonly ILogger<AssistantPushToTalkCoordinator> _logger;

    /// <summary>Whether the hold in progress actually opened a microphone — nothing to transcribe if it did not.</summary>
    private bool _isRecording;

    public AssistantPushToTalkCoordinator(
        GlobalHotkeyCoordinator hotkeys,
        IAssistantSessionHost assistant,
        VoiceOverlayCoordinator overlay,
        IVoicePushToTalkService pushToTalk,
        ILogger<AssistantPushToTalkCoordinator> logger,
        IOpenMicState? openMicState = null)
    {
        _hotkeys = hotkeys;
        _assistant = assistant;
        _overlay = overlay;
        _pushToTalk = pushToTalk;
        _openMicState = openMicState;
        _logger = logger;

        // Subscribed once, for the life of the app, and filtered on this key's own id: the hotkey coordinator
        // re-arms in place rather than handing out a new event source, so there is no re-subscription here to
        // accidentally do twice and double every hold.
        _hotkeys.Pressed += (_, id) => { if (id == GlobalHotkeys.AssistantPushToTalk) { _OnHoldStarted(); } };
        _hotkeys.Released += (_, id) => { if (id == GlobalHotkeys.AssistantPushToTalk) { _OnHoldEnded(); } };
    }

    /// <summary>
    /// Wires the Options page to the two things a saved change has to reach: the desktop registration (a rebound
    /// key, or the feature going off, has to re-arm) and the assistant itself (switched off mid-sentence, it stops
    /// there). Called by the shell once the view models exist.
    /// </summary>
    public void FollowSettings(AssistantOptionsViewModel options, AssistantIndicatorCoordinator indicator)
    {
        _indicator = indicator;
        options.Saved += (_, _) => _ = _OnSettingsSavedAsync();
    }

    private AssistantIndicatorCoordinator? _indicator;

    private async Task _OnSettingsSavedAsync()
    {
        await _hotkeys.ApplyAsync().ConfigureAwait(false);
        await _assistant.ApplySettingsAsync().ConfigureAwait(false);

        // The microphone too: switching the assistant off has to close an open mic, or it keeps recording a room
        // whose every utterance now goes nowhere.
        if (_openMicState is OpenMicCoordinator openMic)
        {
            await openMic.ApplyAssistantSettingsAsync().ConfigureAwait(false);
        }

        // And the chip appears or disappears with the switch, rather than at the next restart.
        if (_indicator is not null)
        {
            await _indicator.ApplySettingsAsync().ConfigureAwait(false);
        }
    }

    private void _OnHoldStarted() => Dispatcher.UIThread.Post(HandleHoldStarted);

    private void _OnHoldEnded() => Dispatcher.UIThread.Post(() => _ = HandleHoldEndedAsync());

    private void _OnAudioLevelSampled(object? sender, double level) =>
        Dispatcher.UIThread.Post(() => _overlay.PushLevel(level));

    /// <summary>Test seam: the UI-thread logic for a hold starting.</summary>
    internal void HandleHoldStarted()
    {
        // Open-mic is already listening to the assistant continuously; a hold on top of it would send the same
        // sentence twice. Open-mic wins and says so, exactly as the dictation path stands down for it.
        if (_openMicState?.IsListening == true)
        {
            _isRecording = false;
            _overlay.SetPushToTalk(VoiceOverlayState.Unavailable, "The assistant is already listening");
            return;
        }

        // The reason the assistant is unreachable — off, no profile, a failed start — is already resolved and in
        // words on the host. Saying it on the pill rather than only in the log is criterion 1's "with a message
        // that says why": a key that does nothing and explains nothing is indistinguishable from a broken one.
        if (_assistant.Activity == AssistantActivity.Unavailable)
        {
            _isRecording = false;
            _overlay.SetPushToTalk(VoiceOverlayState.Unavailable, _assistant.UnavailableReason ?? "The assistant is not available");
            return;
        }

        // Detached first so a hold cannot stack, whatever the backend does with a repeated key.
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;
        _pushToTalk.AudioLevelSampled += _OnAudioLevelSampled;

        // Talking over the assistant means "listen to me instead" — stop the read-aloud rather than let it
        // narrate through your sentence, the same thing a dictation hold has always done.
        _isRecording = _pushToTalk.BeginHold();
        _overlay.SetPushToTalk(
            _isRecording ? VoiceOverlayState.Listening : VoiceOverlayState.Unavailable,
            _isRecording ? null : "The microphone could not be opened");

        _logger.LogInformation("Assistant push-to-talk hold started: capturing={Capturing}", _isRecording);
    }

    /// <summary>Test seam: the UI-thread logic for a hold ending — transcribe, then hand the words to the assistant.</summary>
    internal async Task HandleHoldEndedAsync()
    {
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;

        // Nothing was captured, so there is nothing to transcribe — and the reason the pill is showing is the one
        // thing worth leaving on screen for the moment the key is still down.
        if (!_isRecording)
        {
            _overlay.SetPushToTalk(null);
            return;
        }

        _overlay.SetPushToTalk(VoiceOverlayState.Transcribing);

        // Only for as long as this hold: first use fetches gigabytes before it can transcribe, and the pill spent
        // that time on a spinner that claimed to be transcribing.
        _pushToTalk.Preparing += _OnPreparing;
        _pushToTalk.Prepared += _OnPrepared;

        try
        {
            // applyCleanup: false — see this class's remarks. One-to-one is the decision, not an oversight.
            var text = await _pushToTalk.EndHoldAsync(applyCleanup: false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                await _assistant.SendAsync(text);
            }
        }
        catch (Exception exception)
        {
            // The pill is the only place the operator is looking, so the failure belongs there and not only in the
            // log — a hold that produced nothing and said nothing reads as the assistant ignoring you.
            _logger.LogWarning(exception, "An assistant push-to-talk hold produced no transcript.");
            _overlay.SetPushToTalk(VoiceOverlayState.Unavailable, "That could not be transcribed");
            return;
        }
        finally
        {
            _pushToTalk.Preparing -= _OnPreparing;
            _pushToTalk.Prepared -= _OnPrepared;
        }

        _overlay.SetPushToTalk(null);
    }

    private void _OnPreparing(object? sender, VoicePreparationProgress step) =>
        Dispatcher.UIThread.Post(() => _overlay.SetPushToTalk(VoiceOverlayState.Preparing, step.Description, step.Fraction));

    private void _OnPrepared(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _overlay.SetPushToTalk(VoiceOverlayState.Transcribing));
}
