namespace Cockpit.App.ViewModels;

/// <summary>
/// A selectable Whisper transcription model in the Options → Voice → Transcribe dropdown: the ggml model name,
/// a short accuracy-vs-load hint, and whether it is the "Custom…" sentinel that reveals a free-text box for any
/// ggml name not in the curated list (e.g. a quantized variant like <c>large-v3-turbo-q5_0</c>).
/// </summary>
public sealed record TranscriptionModelOption(string Name, string Hint, bool IsCustom = false);
