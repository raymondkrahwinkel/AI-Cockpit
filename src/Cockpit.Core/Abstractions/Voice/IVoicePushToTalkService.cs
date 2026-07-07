namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Orchestrates one push-to-talk hold end to end: buffer microphone audio while the hotkey is held,
/// then on release gate it through VAD, transcribe, and (optionally) clean up — the single entry point
/// the session views/view models drive from their KeyDown/KeyUp handlers.
/// </summary>
public interface IVoicePushToTalkService
{
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
