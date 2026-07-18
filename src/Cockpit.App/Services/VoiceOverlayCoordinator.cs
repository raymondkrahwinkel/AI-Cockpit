using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

/// <summary>
/// The one thing that decides what the voice overlay says. Three sources report into it — a push-to-talk hold,
/// open-mic dictation, and read-aloud — and none of them may write the pill directly: they each know their own
/// half and nothing about the others, so left to themselves they overwrite each other. The pill vanishing
/// mid-transcription because an unrelated source went idle is the failure this exists to make impossible.
/// </summary>
/// <remarks>
/// The rule, in one line: <b>speech-to-text owns the pill, and a hold owns it over open-mic.</b>
/// <list type="bullet">
/// <item><b>STT before TTS</b> (Raymond, 2026-07-15). What you are saying outranks what the cockpit is saying:
/// dictation is a thing you are doing right now and read-aloud is a thing you can hear anyway. In practice they
/// rarely collide — open-mic pauses itself while playback runs (barge-in) — but a hold during read-aloud is
/// exactly the barge-in case, and there the hold has to win.</item>
/// <item><b>A hold before open-mic.</b> Both are dictation, but one of them you asked for by holding a key.</item>
/// </list>
/// Sources report their own state and nothing else. Whether that state is the one on screen is not their
/// business, which is what keeps this rule in one place instead of spread across three coordinators.
/// </remarks>
public sealed class VoiceOverlayCoordinator(VoiceOverlayViewModel overlay, IVoiceOverlayPresenter presenter) : ISingletonService
{
    private VoiceOverlayState? _pushToTalk;
    private VoiceOverlayState? _openMic;
    private VoiceOverlayState? _readAloud;
    private string _status = string.Empty;
    private double? _progress;

    /// <summary>The pill's view model — the overlay window binds to this.</summary>
    public VoiceOverlayViewModel Overlay => overlay;

    /// <summary>What the push-to-talk hold has to say, or null once the hold is over.</summary>
    public void SetPushToTalk(VoiceOverlayState? state, string? status = null, double? progress = null)
    {
        _pushToTalk = state;
        _Remember(state, status, progress);
        _Apply();
    }

    /// <summary>What open-mic dictation has to say, or null while it is listening to nothing in particular.</summary>
    /// <remarks>
    /// Null is the resting state, not "off": open-mic listens continuously, and a pill that sat there the whole
    /// time would say nothing except that the feature is on. It appears when the VAD hears speech start, which is
    /// the moment it has something to report.
    /// </remarks>
    public void SetOpenMic(VoiceOverlayState? state)
    {
        _openMic = state;
        _Remember(state, status: null, progress: null);
        _Apply();
    }

    /// <summary>
    /// What read-aloud has to say, or null when it is idle: <see cref="VoiceOverlayState.Preparing"/> while it is
    /// synthesizing (text-to-sound, before any audio — with a status word), <see cref="VoiceOverlayState.Speaking"/>
    /// once audio is actually playing. Shown only when no dictation is in progress — see the class remarks.
    /// </summary>
    public void SetReadAloud(VoiceOverlayState? state, string? status = null)
    {
        _readAloud = state;
        _Remember(state, status, progress: null);
        _Apply();
    }

    /// <summary>
    /// A microphone level for the waveform. Both dictation sources feed the same microphone, so whichever owns
    /// the pill is the one being drawn — the view model already drops a level that arrives while the pill is not
    /// listening, which is what keeps a late frame from a finished hold out of the next one.
    /// </summary>
    public void PushLevel(double level) => overlay.PushLevel(level);

    // Preparing and Unavailable carry words; the view model drops them the moment the state moves on, so they
    // have to be re-applied every time this recomputes — a source's status must not evaporate because another
    // source reported something unrelated.
    private void _Remember(VoiceOverlayState? state, string? status, double? progress)
    {
        if (state is VoiceOverlayState.Preparing or VoiceOverlayState.Unavailable)
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
        if (state is VoiceOverlayState.Preparing or VoiceOverlayState.Unavailable)
        {
            overlay.StatusText = _status;
            overlay.Progress = _progress;
        }

        overlay.State = state;
        presenter.Show();
    }
}
