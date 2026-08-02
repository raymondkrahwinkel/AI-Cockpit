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

    /// <summary>
    /// Loads the voice ahead of the reply that is coming (AC-603). A spoken answer arrives seconds after the
    /// operator stops talking, and a cold voice turns that answer into a second wait. Best-effort, like the
    /// transcriber's.
    /// </summary>
    Task WarmUpAsync(CancellationToken cancellationToken = default);
}
