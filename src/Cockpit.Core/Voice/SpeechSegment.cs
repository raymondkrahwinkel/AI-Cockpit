namespace Cockpit.Core.Voice;

/// <summary>
/// A run of read-aloud sentences that all speak in one <see cref="Language"/> (an ISO-639-1 code such as
/// "en" or "nl"). Read-aloud splits mixed Dutch/English assistant prose into per-language segments; the
/// single Supertonic voice speaks each one in the tagged language (passed to the engine as data), so the
/// timbre stays constant while the pronunciation follows the language — no voice switch mid-reply.
/// </summary>
public sealed record SpeechSegment(IReadOnlyList<string> Sentences, string Language);
