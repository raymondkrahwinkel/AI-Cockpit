namespace Cockpit.App.ViewModels;

// Voice transcription choice: curated ggml model, AC-68's per-machine Auto choice, or Custom free-text sentinel.
public sealed record TranscriptionModelOption(string Name, string Hint, bool IsCustom = false, bool IsAuto = false);
