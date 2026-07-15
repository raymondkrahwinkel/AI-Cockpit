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
    /// Raised while a <see cref="TranscribeAsync"/> call is still getting ready — the model or a GPU runtime
    /// coming down, or the model being loaded. Initializing lazily is what keeps voice free when it is off, but
    /// it also means the first dictation waits on gigabytes, and a wait nobody narrates is indistinguishable
    /// from a hang. Fires on whichever thread the work runs on — subscribers marshal to the UI themselves.
    /// </summary>
    event EventHandler<VoicePreparationProgress>? Preparing;

    /// <summary>
    /// Raised when preparation is done and the samples are actually being transcribed — but only on a call that
    /// had something to prepare. Without it the last <see cref="Preparing"/> line would sit on screen through
    /// the transcription itself, which just moves the lie one step along instead of ending it.
    /// </summary>
    event EventHandler? Prepared;

    /// <summary>
    /// Which native runtime actually ended up loaded, once known (after the first
    /// <see cref="TranscribeAsync"/> call initializes the model) — surfaced for diagnostics/settings
    /// display so "auto" preference is not a black box.
    /// </summary>
    WhisperRuntimeBackend? ActiveBackend { get; }
}
