namespace Cockpit.Core.Voice;

// A run of read-aloud sentences that all speak in one `Language` (an ISO-639-1 code such as
// "en" or "nl") — the unit the playback queue synthesizes and plays. The single Supertonic voice speaks each
// segment in its own language (passed to the engine as data), so a batch of several can change pronunciation
// without changing timbre: no voice switch mid-reply.
//
// Today every batch the cockpit enqueues is one segment in the operator's read-aloud language; the marker-based
// splitter that produced mixed batches went with the naturalization LLM that emitted the markers (AC-546). The
// multi-segment shape is kept because it is what the queue plays, not because anything currently fills it.
public sealed record SpeechSegment(IReadOnlyList<string> Sentences, string Language);
