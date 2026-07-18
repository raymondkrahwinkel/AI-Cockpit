using Cockpit.Core.Voice;

namespace Cockpit.Core.Abstractions.Voice;

/// <summary>Synthesizes speech for a single utterance (a sentence-sized chunk of assistant prose).</summary>
public interface ITextToSpeechService
{
    /// <summary>
    /// Synthesizes <paramref name="text"/> with speaker <paramref name="speakerId"/> in
    /// <paramref name="language"/> (an ISO-639-1 code such as "en"/"nl"). One Supertonic voice covers every
    /// language, so the speaker (timbre) is constant across a reply while the language varies per segment.
    /// </summary>
    Task<TtsAudio> SynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken = default);
}
