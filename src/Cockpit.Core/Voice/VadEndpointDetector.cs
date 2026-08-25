namespace Cockpit.Core.Voice;

// AC-1013: Endpointing state machine for open-mic dictation (#PLANNED open-mic/VAD): fed per-frame "is this speech?" observations, decides where an utterance starts (enough contiguous speech) and ends (trailing silence timeout). Pure/deterministic — no audio, clock, or threading — so boundary logic is unit-testable apart from mic capture and the VAD model.
public sealed class VadEndpointDetector
{
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _minSpeechToStart;

    private bool _inSpeech;
    private TimeSpan _contiguousSpeech;
    private TimeSpan _trailingSilence;

    // `silenceTimeout`: How long the trailing silence must last to close an utterance (the endpointing pause, e.g. 800ms).
    // `minSpeechToStart`: How much contiguous speech must accumulate before an utterance starts, guarding a single spurious speech frame from opening one.
    public VadEndpointDetector(TimeSpan silenceTimeout, TimeSpan minSpeechToStart)
    {
        _silenceTimeout = silenceTimeout;
        _minSpeechToStart = minSpeechToStart;
    }

    // True while an utterance is open — between a `VadEndpointSignal.SpeechStarted` and its `VadEndpointSignal.SpeechEnded`.
    public bool IsInSpeech => _inSpeech;

    // Feeds one observation and returns the boundary it crosses, if any.
    public VadEndpointSignal Observe(bool isSpeech, TimeSpan frameDuration)
    {
        if (!_inSpeech)
        {
            if (!isSpeech)
            {
                // A gap resets the run: the speech that starts an utterance must be contiguous, so a lone
                // noise blip between silences never opens one.
                _contiguousSpeech = TimeSpan.Zero;
                return VadEndpointSignal.None;
            }

            _contiguousSpeech += frameDuration;
            if (_contiguousSpeech < _minSpeechToStart)
            {
                return VadEndpointSignal.None;
            }

            _inSpeech = true;
            _trailingSilence = TimeSpan.Zero;
            return VadEndpointSignal.SpeechStarted;
        }

        if (isSpeech)
        {
            // More speech (or speech resuming after a pause shorter than the timeout) keeps the utterance open.
            _trailingSilence = TimeSpan.Zero;
            return VadEndpointSignal.None;
        }

        _trailingSilence += frameDuration;
        if (_trailingSilence < _silenceTimeout)
        {
            return VadEndpointSignal.None;
        }

        _inSpeech = false;
        _contiguousSpeech = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
        return VadEndpointSignal.SpeechEnded;
    }

    // Drops any in-progress utterance and returns to waiting for speech — used when open-mic pauses (e.g. while read-aloud plays) so a resumed capture starts clean.
    public void Reset()
    {
        _inSpeech = false;
        _contiguousSpeech = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
    }
}
