namespace Cockpit.Core.Voice;

/// <summary>
/// The endpointing state machine for open-mic dictation (#PLANNED open-mic/VAD): fed a stream of
/// per-frame "is this speech?" observations, it decides where one spoken utterance begins and ends —
/// start once enough contiguous speech has accumulated, end once the trailing silence reaches the
/// timeout. Pure and deterministic (no audio, no clock, no threading): the caller supplies each
/// observation and its duration, which makes the boundary logic fully unit-testable in isolation from
/// the mic capture and the VAD model that produce those observations.
/// </summary>
public sealed class VadEndpointDetector
{
    private readonly TimeSpan _silenceTimeout;
    private readonly TimeSpan _minSpeechToStart;

    private bool _inSpeech;
    private TimeSpan _contiguousSpeech;
    private TimeSpan _trailingSilence;

    /// <param name="silenceTimeout">How long the trailing silence must last to close an utterance (the endpointing pause, e.g. 800ms).</param>
    /// <param name="minSpeechToStart">How much contiguous speech must accumulate before an utterance starts, guarding a single spurious speech frame from opening one.</param>
    public VadEndpointDetector(TimeSpan silenceTimeout, TimeSpan minSpeechToStart)
    {
        _silenceTimeout = silenceTimeout;
        _minSpeechToStart = minSpeechToStart;
    }

    /// <summary>True while an utterance is open — between a <see cref="VadEndpointSignal.SpeechStarted"/> and its <see cref="VadEndpointSignal.SpeechEnded"/>.</summary>
    public bool IsInSpeech => _inSpeech;

    /// <summary>Feeds one observation and returns the boundary it crosses, if any.</summary>
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

    /// <summary>Drops any in-progress utterance and returns to waiting for speech — used when open-mic pauses (e.g. while read-aloud plays) so a resumed capture starts clean.</summary>
    public void Reset()
    {
        _inSpeech = false;
        _contiguousSpeech = TimeSpan.Zero;
        _trailingSilence = TimeSpan.Zero;
    }
}
