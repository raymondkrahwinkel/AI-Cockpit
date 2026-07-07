namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Gates a captured utterance on whether it actually contains speech, so silence/room-noise from a
/// push-to-talk hold never reaches (and never wastes time in) the STT model.
/// </summary>
public interface IVoiceActivityDetector
{
    /// <summary>16 kHz mono float32 samples in [-1, 1]. True when at least one speech segment was detected.</summary>
    Task<bool> HasSpeechAsync(float[] samples, CancellationToken cancellationToken = default);
}
