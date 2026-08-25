namespace Cockpit.Core.Voice;

// What the voice pipeline is doing while a dictation waits on it (model/GPU runtime download, model load).
// First use fetches gigabytes before a word can be transcribed, and without this the operator watches a
// spinner labelled "Transcribing…" for minutes. `Fraction` is null when the total is unknown (ggml download has no length) rather than inventing a position.
public sealed record VoicePreparationProgress(string Description, double? Fraction = null);
