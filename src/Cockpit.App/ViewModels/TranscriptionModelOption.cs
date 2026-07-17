namespace Cockpit.App.ViewModels;

/// <summary>
/// A selectable Whisper transcription model in the Options → Voice → Transcribe dropdown: the display name, a
/// short accuracy-vs-load hint, and which kind of item it is. Most are curated ggml models; <see cref="IsAuto"/>
/// is the "Auto ★" item that follows the per-machine recommendation (AC-68 slice 2); <see cref="IsCustom"/> is the
/// "Custom…" sentinel that reveals a free-text box for any ggml name not in the list (e.g. <c>large-v3-turbo-q5_0</c>).
/// </summary>
public sealed record TranscriptionModelOption(string Name, string Hint, bool IsCustom = false, bool IsAuto = false);
