using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// The one thing that decides what the voice overlay says. Three sources (push-to-talk, open-mic, read-aloud) report
// into it rather than writing the pill directly, since left to themselves they overwrite each other. Rule: STT owns
// the pill over TTS (a hold during read-aloud is the barge-in case and must win), and a hold owns it over open-mic.
public sealed class VoiceOverlayCoordinator(VoiceOverlayViewModel overlay, IVoiceOverlayPresenter presenter) : ISingletonService
{
    private VoiceOverlayState? _pushToTalk;
    private VoiceOverlayState? _openMic;
    private VoiceOverlayState? _readAloud;
    private string _status = string.Empty;
    private double? _progress;

    // Bumped by every report a hold makes, so a linger started by an earlier one can tell it is stale and leave
    // the pill a newer hold now owns alone — see `ShowPushToTalkThenClear`.
    private int _pushToTalkGeneration;

    // The pill's view model — the overlay window binds to this.
    public VoiceOverlayViewModel Overlay => overlay;

    // What the push-to-talk hold has to say, or null once the hold is over.
    public void SetPushToTalk(VoiceOverlayState? state, string? status = null, double? progress = null)
    {
        _pushToTalkGeneration++;
        _pushToTalk = state;
        _Remember(state, status, progress);
        _Apply();
    }

    // A hold's own explanation of why it produced nothing, which clears itself after `linger`. The one report
    // nothing comes back to take off the pill: every other state ends when its source says so, and this one would
    // sit there for good — masking read-aloud, which a hold's report outranks.
    public void ShowPushToTalkThenClear(VoiceOverlayState state, string message, TimeSpan linger)
    {
        SetPushToTalk(state, message);
        PendingPushToTalkClear = _ClearAfterLingerAsync(_pushToTalkGeneration, linger);
    }

    // Test seam: the linger the last self-clearing message started, so a test can await the clear rather than
    // sleep for longer than it hopes it takes. Completed when there is none — the resting state.
    public Task PendingPushToTalkClear { get; private set; } = Task.CompletedTask;

    private async Task _ClearAfterLingerAsync(int generation, TimeSpan linger)
    {
        await Task.Delay(linger);

        if (generation == _pushToTalkGeneration)
        {
            SetPushToTalk(null);
        }
    }

    // What open-mic dictation has to say, or null while listening to nothing in particular. Null is the resting
    // state, not "off": a pill sitting there the whole time would say nothing but that the feature is on, so it
    // appears only once the VAD hears speech start.
    public void SetOpenMic(VoiceOverlayState? state)
    {
        _openMic = state;
        _Remember(state, status: null, progress: null);
        _Apply();
    }

    // What read-aloud has to say, or null when it is idle: `VoiceOverlayState.Preparing` while it is
    // synthesizing (text-to-sound, before any audio — with a status word), `VoiceOverlayState.Speaking`
    // once audio is actually playing. Shown only when no dictation is in progress — see the class remarks.
    public void SetReadAloud(VoiceOverlayState? state, string? status = null)
    {
        _readAloud = state;
        _Remember(state, status, progress: null);
        _Apply();
    }

    // A microphone level for the waveform. Both dictation sources feed the same microphone, so whichever owns
    // the pill is the one being drawn — the view model already drops a level that arrives while the pill is not
    // listening, which is what keeps a late frame from a finished hold out of the next one.
    public void PushLevel(double level)
    {
        overlay.PushLevel(level);
        LevelSampled?.Invoke(this, level);
    }

    // The same level, for anything that draws the microphone besides the pill (AC-543: the assistant chip's line).
    // Announced from here rather than subscribed at each source, since all three already funnel through
    // `PushLevel` — a second set of subscriptions would be three places to keep in step.
    public event EventHandler<double>? LevelSampled;

    // Some states carry words (`VoiceOverlayViewModel.CarriesWords`); the view model drops them the
    // moment the state moves on, so they have to be re-applied every time this recomputes — a source's status must
    // not evaporate because another source reported something unrelated.
    private void _Remember(VoiceOverlayState? state, string? status, double? progress)
    {
        if (VoiceOverlayViewModel.CarriesWords(state))
        {
            _status = status ?? _status;
            _progress = progress;
        }
    }

    private void _Apply()
    {
        var state = _pushToTalk
            ?? _openMic
            ?? _readAloud
            ?? VoiceOverlayState.Hidden;

        if (state == VoiceOverlayState.Hidden)
        {
            overlay.State = VoiceOverlayState.Hidden;
            presenter.Hide();
            return;
        }

        // Before the state: the view model clears the text on any state that has nothing to say, so setting it
        // afterwards would be setting it into a state that just threw it away.
        if (VoiceOverlayViewModel.CarriesWords(state))
        {
            overlay.StatusText = _status;
            overlay.Progress = _progress;
        }

        overlay.State = state;
        presenter.Show();
    }
}
