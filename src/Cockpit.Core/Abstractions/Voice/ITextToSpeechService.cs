using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>Synthesizes speech for a single utterance (a sentence-sized chunk of assistant prose).</summary>
public interface ITextToSpeechService
{
    /// <summary>Synthesizes <paramref name="text"/> in the voice identified by <paramref name="voiceId"/>.</summary>
    Task<TtsAudio> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default);
}
