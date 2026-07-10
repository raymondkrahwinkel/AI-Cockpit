namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Open-mic dictation: listens to the microphone continuously and detects utterance boundaries itself
/// (VAD endpointing) rather than requiring a push-to-talk hold. Each finished utterance is transcribed
/// and surfaced via <see cref="UtteranceTranscribed"/>; the coordinator decides which session receives
/// it. A single shared instance for the whole (single-user) cockpit.
/// </summary>
public interface IOpenMicListener
{
    /// <summary>Raised on the capture thread with the raw transcript once an utterance ends. Subscribers marshal onto the UI thread themselves.</summary>
    event EventHandler<string>? UtteranceTranscribed;

    /// <summary>Raised once per captured frame with a 0..1 loudness level, so the voice overlay can show a live waveform. Fires on the capture thread.</summary>
    event EventHandler<double>? AudioLevelSampled;

    /// <summary>Starts the continuous capture loop. No-op if already running.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the capture loop and releases the microphone. No-op if not running.</summary>
    Task StopAsync();

    /// <summary>Suspends detection and drops any half-formed utterance — used while read-aloud plays so the mic never transcribes the cockpit's own text-to-speech (barge-in guard).</summary>
    void Pause();

    /// <summary>Resumes detection after a <see cref="Pause"/>.</summary>
    void Resume();
}
