using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Transcribes a complete utterance (buffered while push-to-talk was held) to text. Implementations
/// own the Whisper model/runtime lifecycle and initialize lazily on first use — voice is opt-in, so
/// nothing heavy loads until the operator actually dictates.
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>16 kHz mono float32 samples in [-1, 1] (the Whisper target format) in, transcribed text out.</summary>
    Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which native runtime actually ended up loaded, once known (after the first
    /// <see cref="TranscribeAsync"/> call initializes the model) — surfaced for diagnostics/settings
    /// display so "auto" preference is not a black box.
    /// </summary>
    WhisperRuntimeBackend? ActiveBackend { get; }
}
