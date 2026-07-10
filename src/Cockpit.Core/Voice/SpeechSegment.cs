namespace Cockpit.Core.Voice;

/// <summary>
/// A run of read-aloud sentences that all speak in one <see cref="VoiceId"/>. Read-aloud splits mixed
/// Dutch/English assistant prose into per-language segments so each is synthesized by the matching Piper
/// voice — no single sherpa-onnx voice covers both languages, so the routing layer switches voices per
/// language segment instead (research: Cockpit-TTS-CodeSwitching-2026-07-10).
/// </summary>
public sealed record SpeechSegment(IReadOnlyList<string> Sentences, string VoiceId);
