namespace Cockpit.Core.Abstractions.Voice;

/// <summary>
/// Passes a raw STT transcript through a local OpenAI-compatible LLM (Ollama or LM Studio) for
/// punctuation/filler cleanup. Implementations must fall back to returning <paramref name="rawText"/>
/// unchanged whenever the cleanup is unavailable or its output looks untrustworthy (see
/// <c>TranscriptCleanupGuard</c>) — never surface a server failure to the caller as an error.
/// </summary>
public interface ITranscriptCleanupService
{
    Task<string> CleanupAsync(string rawText, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites assistant text into natural spoken sentences for read-aloud (#35) — dropping code, paths,
    /// URLs and markdown and smoothing technical phrasing — via the same local LLM. Falls back to
    /// the original text whenever the model is unavailable or returns nothing; never throws to the caller.
    /// </summary>
    Task<string> NaturalizeForSpeechAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarizes assistant text to its essence for read-aloud (#35) — shorter than the reply, but preserving
    /// every number, decision, warning and action item and inventing nothing — via the same local LLM, with
    /// the same language tagging as <see cref="NaturalizeForSpeechAsync"/>. Falls back to the original text
    /// whenever the model is unavailable or returns nothing; never throws to the caller.
    /// </summary>
    Task<string> SummarizeForSpeechAsync(string text, CancellationToken cancellationToken = default);
}
