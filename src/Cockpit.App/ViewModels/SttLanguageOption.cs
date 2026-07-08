namespace Cockpit.App.ViewModels;

/// <summary>A selectable dictation language for speech-to-text: display label plus the Whisper language code ("auto", "nl", "en", …).</summary>
public sealed record SttLanguageOption(string Label, string Code);
