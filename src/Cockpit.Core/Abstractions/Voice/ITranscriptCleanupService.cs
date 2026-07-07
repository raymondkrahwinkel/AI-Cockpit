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
}
