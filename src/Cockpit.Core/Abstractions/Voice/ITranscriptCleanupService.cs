namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Passes a raw STT transcript through the local Ollama model for punctuation/filler cleanup.
/// Implementations must fall back to returning <paramref name="rawText"/> unchanged whenever the
/// cleanup is unavailable or its output looks untrustworthy (see <c>TranscriptCleanupGuard</c>) —
/// never surface an Ollama failure to the caller as an error.
/// </summary>
public interface ITranscriptCleanupService
{
    Task<string> CleanupAsync(string rawText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites assistant text into natural spoken sentences for read-aloud (#35) — dropping code, paths,
    /// URLs and markdown and smoothing technical phrasing — via the same local Ollama model. Falls back to
    /// the original text whenever the model is unavailable or returns nothing; never throws to the caller.
    /// </summary>
    Task<string> NaturalizeForSpeechAsync(string text, CancellationToken cancellationToken = default);
}
