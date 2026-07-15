using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Orchestrates one push-to-talk hold end to end: buffer microphone audio while the hotkey is held,
/// then on release gate it through VAD, transcribe, and (optionally) clean up — the single entry point
/// the session views/view models drive from their KeyDown/KeyUp handlers.
/// </summary>
public interface IVoicePushToTalkService
{
    /// <summary>
    /// Raised once per captured microphone frame during a hold, carrying a 0..1 loudness level, so the
    /// voice overlay can show a live waveform of what the mic is picking up (#34b). Fires on the capture
    /// thread — subscribers marshal onto the UI thread themselves.
    /// </summary>
    event EventHandler<double>? AudioLevelSampled;

    /// <summary>
    /// Forwarded from <see cref="ISpeechToTextService.Preparing"/>: what a hold is waiting on before it can
    /// transcribe, which on first use is a download of gigabytes. It sits beside
    /// <see cref="AudioLevelSampled"/> because the views and view models that show hold status already hold
    /// this interface and nothing else new. Fires off the UI thread — subscribers marshal themselves.
    /// </summary>
    event EventHandler<VoicePreparationProgress>? Preparing;

    /// <summary>Forwarded from <see cref="ISpeechToTextService.Prepared"/>: preparation is over and the hold is really being transcribed now.</summary>
    event EventHandler? Prepared;

    /// <summary>
    /// Starts buffering microphone audio for a new hold. Returns false (no-op) if a hold is already in
    /// progress — guards against OS key-repeat re-triggering a capture restart mid-hold.
    /// </summary>
    bool BeginHold();

    /// <summary>
    /// Stops buffering and runs VAD gating + STT + optional cleanup, returning the final text (empty
    /// string when VAD found no speech or STT returned nothing). Throws <see cref="InvalidOperationException"/>
    /// if called without a preceding successful <see cref="BeginHold"/>.
    /// </summary>
    Task<string> EndHoldAsync(bool applyCleanup, CancellationToken cancellationToken = default);
}
