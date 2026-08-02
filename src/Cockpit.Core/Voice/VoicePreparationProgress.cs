namespace Cockpit.Core.Voice;

// What the voice pipeline is doing while a dictation waits on it — the model or a GPU runtime coming down,
// or the model being loaded. First use fetches gigabytes before a word can be transcribed, and without this
// the operator watches a spinner labelled "Transcribing…" for minutes while nothing is being transcribed.
//
// `Description`: UI text, ready to show ("Downloading speech model — 412 MB").
// `Fraction`:
// Progress in 0..1, or null when the total is genuinely unknown — the ggml downloader hands out a stream
// without a length, and a bar that invents its own position is worse than no bar.
public sealed record VoicePreparationProgress(string Description, double? Fraction = null);
