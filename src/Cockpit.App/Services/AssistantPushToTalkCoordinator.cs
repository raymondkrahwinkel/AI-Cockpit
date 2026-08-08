using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Voice;

namespace Cockpit.App.Services;

// Routes the assistant hotkey (F10) to the assistant instance (AC-543) — the twin of
// `VoicePushToTalkCoordinator`, which routes F9 to the selected session and is left untouched.
// *Why not one coordinator with a flag.* The two paths differ in every step that matters: F9 puts text in a
// session's composer for you to read before you send it, F10 hands it straight to the assistant. Folding them
// together would mean a branch at each of those steps, and the failure mode of getting one branch wrong is the
// exact thing the indicator exists to prevent — words landing in the wrong place.
//
// *Straight through, no cleanup.* What Whisper heard is what the assistant gets (decision 10): this
// coordinator hands `IVoicePushToTalkService.EndHoldAsync`'s transcript to
// `IAssistantSessionHost.SendAsync` unaltered. Tidying it is the system prompt's job, and a pass here
// would be a second copy of it — running on every utterance, for a model that reads through filler words on its
// own.
//
// Threading matches `VoicePushToTalkCoordinator`'s: the hotkey events arrive on the backend's own
// thread and everything touching the view models is marshalled onto the UI thread first. The
// `Handle…` methods are the UI-thread logic and the seam the tests drive, since pumping a real Avalonia
// dispatcher loop from a unit test is not practical.
public sealed class AssistantPushToTalkCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly IAssistantSessionHost _assistant;
    private readonly VoiceOverlayCoordinator _overlay;
    private readonly IVoicePushToTalkService _pushToTalk;
    private readonly IVoicePlaybackQueue _playbackQueue;
    private readonly IOpenMicState? _openMicState;

    // Warmed on the press alongside the transcriber (AC-603): the reply is spoken seconds after the release, and
    // a cold voice makes the answer a second wait. Optional — a cockpit with no voice simply warms nothing.
    private readonly ITextToSpeechService? _textToSpeech;
    private readonly ILogger<AssistantPushToTalkCoordinator> _logger;

    // Whether the hold in progress actually opened a microphone — nothing to transcribe if it did not.
    private bool _isRecording;

    private static readonly TimeSpan DefaultMessageLinger = TimeSpan.FromSeconds(4);

    // How long a failed hold's own explanation stays on the pill before making way for whatever wants it next.
    // Long enough to read a sentence; short enough that a read-aloud starting in the meantime is not left hidden
    // behind it for good — see `_ShowThenClear`.
    private readonly TimeSpan _messageLinger;

    // Bumped at the start of every hold (`HandleHoldStarted`), so a linger from an earlier, unrelated
    // hold can tell it is stale and skip clearing a pill a newer hold now owns.
    private int _pushToTalkGeneration;

    // `messageLinger`:
    // Overridden only by the tests, which cannot wait four seconds to watch a linger clear — same reasoning as
    // `ScheduledResumeCoordinator`'s tick interval.
    public AssistantPushToTalkCoordinator(
        GlobalHotkeyCoordinator hotkeys,
        IAssistantSessionHost assistant,
        VoiceOverlayCoordinator overlay,
        IVoicePushToTalkService pushToTalk,
        ILogger<AssistantPushToTalkCoordinator> logger,
        IVoicePlaybackQueue playbackQueue,
        IOpenMicState? openMicState = null,
        ITextToSpeechService? textToSpeech = null,
        TimeSpan? messageLinger = null)
    {
        _hotkeys = hotkeys;
        _assistant = assistant;
        _overlay = overlay;
        _pushToTalk = pushToTalk;
        _playbackQueue = playbackQueue;
        _openMicState = openMicState;
        _textToSpeech = textToSpeech;
        _logger = logger;
        _messageLinger = messageLinger ?? DefaultMessageLinger;

        // Subscribed once, for the life of the app, and filtered on this key's own id: the hotkey coordinator
        // re-arms in place rather than handing out a new event source, so there is no re-subscription here to
        // accidentally do twice and double every hold.
        _hotkeys.Pressed += (_, id) => { if (id == GlobalHotkeys.AssistantPushToTalk) { _OnHoldStarted(); } };
        _hotkeys.Released += (_, id) => { if (id == GlobalHotkeys.AssistantPushToTalk) { _OnHoldEnded(); } };
    }

    // Wires the Options page to the two things a saved change has to reach: the desktop registration (a rebound
    // key, or the feature going off, has to re-arm) and the assistant itself (switched off mid-sentence, it stops
    // there). Called by the shell once the view models exist.
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

    // Test seam: the UI-thread logic for a hold starting.
    internal void HandleHoldStarted()
    {
        // Invalidates any linger still pending from an earlier hold's failure message (see _ShowThenClearAsync):
        // this hold's own state is what belongs on the pill from here on, not a delayed clear stepping on it.
        _pushToTalkGeneration++;

        // Open-mic already sends to the assistant, so unlike F9 there is nothing for a hold to take back. Silent
        // and still armed (AC-627): it is not a fault, and an unregistered F10 would fall through to the app
        // underneath.
        if (_openMicState?.IsListening == true)
        {
            _isRecording = false;
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

        // Talking over the assistant means "listen to me instead" — stop the read-aloud rather than let it narrate
        // through your sentence.
        //
        // This line is the one that was missing while the comment above it claimed otherwise, and it is the whole
        // bug: holding the key to interrupt left the assistant reading its previous answer out over the top of the
        // next question. Unconditional, unlike open-mic's barge-in (AC-9), which weighs a VAD threshold because it
        // has to tell speech from a cough. A held hotkey is not ambiguous — somebody pressed a key to talk.
        _playbackQueue.StopAll();

        _isRecording = _pushToTalk.BeginHold();
        _overlay.SetPushToTalk(
            _isRecording ? VoiceOverlayState.Listening : VoiceOverlayState.Unavailable,
            _isRecording ? null : "The microphone could not be opened");

        // Say out loud that the assistant is the one listening. The pill this coordinator just wrote to is shared
        // with dictation and open-mic, so without this the chip read a held F10 as dictation — and told the
        // operator to release F9.
        if (_isRecording)
        {
            _assistant.ReportHoldListening(true);
            _WarmUpWhileTheySpeak();
        }

        _logger.LogInformation("Assistant push-to-talk hold started: capturing={Capturing}", _isRecording);
    }

    // Starts everything this hold is going to need while the operator is still talking into it (AC-602, AC-603):
    // the session, the transcriber, and the voice that will read the answer back.
    // The press is the only free window there is. Afterwards the operator has stopped speaking and every one of
    // these is a silence they are waiting through — which is the same silence the assistant is built to avoid.
    // None of it is awaited and none of it is reported: a warm-up that fails leaves first use to do what it
    // already does, including saying what went wrong.
    private void _WarmUpWhileTheySpeak()
    {
        _ = _assistant.EnsureStartedAsync();
        _ = _pushToTalk.WarmUpAsync();
        _ = _textToSpeech?.WarmUpAsync() ?? Task.CompletedTask;
    }

    // Test seam: the UI-thread logic for a hold ending — transcribe, then hand the words to the assistant.
    internal async Task HandleHoldEndedAsync()
    {
        _pushToTalk.AudioLevelSampled -= _OnAudioLevelSampled;

        // The hold is over whatever comes next: a transcript hands over to SendAsync (which reports Thinking), and
        // an empty one has to fall back to Ready rather than leave the chip listening to nobody.
        _assistant.ReportHoldListening(false);

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
            // Straight through, one-to-one — see this class's remarks.
            var text = await _pushToTalk.EndHoldAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                // The most common way this fails, and until now the only one that said nothing at all. A hold
                // shorter than a second or two gives the voice-activity detector too little to find speech in, so
                // it discards the capture and returns nothing — correctly. What was missing is anyone telling the
                // operator: the chip flicked back to Ready, no words appeared, and there is no composer here to
                // show an empty result the way the dictation path does. Held against a live microphone that is
                // indistinguishable from an assistant that ignored you, and it costs an attempt every time.
                //
                // Only on this path, deliberately. F9 hands its transcript straight to a session's composer, where
                // "nothing appeared in the box" is at least visible; there is no shared point below this one where
                // both could be told, because that path never sees the text at all.
                _ShowThenClear("No speech heard — keep holding the key while you talk, then let go.");
                return;
            }

            await _assistant.SendAsync(text);
        }
        catch (Exception exception)
        {
            // The pill is the only place the operator is looking, so the failure belongs there and not only in the
            // log — a hold that produced nothing and said nothing reads as the assistant ignoring you.
            _logger.LogWarning(exception, "An assistant push-to-talk hold produced no transcript.");
            _ShowThenClear("That could not be transcribed");
            return;
        }
        finally
        {
            _pushToTalk.Preparing -= _OnPreparing;
            _pushToTalk.Prepared -= _OnPrepared;
        }

        _overlay.SetPushToTalk(null);
    }

    // Puts a failed hold's explanation on the pill and, after `MessageLinger`, clears it — unless a
    // newer hold has since taken the pill over (see `_pushToTalkGeneration`). Without the clear this
    // message never went away on its own: read-aloud starting afterwards is masked
    // (`VoiceOverlayCoordinator` puts a hold's own report ahead of read-aloud's) and stayed that way for
    // good, since nothing else in this file writes to the pill until the next hold.
    private void _ShowThenClear(string message)
    {
        var generation = _pushToTalkGeneration;
        _overlay.SetPushToTalk(VoiceOverlayState.Unavailable, message);
        PendingLingerClear = _ClearAfterLingerAsync(generation);
    }

    // Test seam: the linger started by the last failed hold, so a test can await the clear itself instead of
    // sleeping for longer than it hopes the linger takes. Completed when there is none — the resting state, and
    // what a test that never triggered a failure message awaits.
    internal Task PendingLingerClear { get; private set; } = Task.CompletedTask;

    private async Task _ClearAfterLingerAsync(int generation)
    {
        await Task.Delay(_messageLinger);

        if (generation == _pushToTalkGeneration)
        {
            _overlay.SetPushToTalk(null);
        }
    }

    private void _OnPreparing(object? sender, VoicePreparationProgress step) =>
        Dispatcher.UIThread.Post(() => _overlay.SetPushToTalk(VoiceOverlayState.Preparing, step.Description, step.Fraction));

    private void _OnPrepared(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _overlay.SetPushToTalk(VoiceOverlayState.Transcribing));
}
