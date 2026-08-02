namespace Cockpit.App.ViewModels;

// A selectable dictation language for speech-to-text: display label plus the Whisper language code ("auto", "nl", "en", …).
public sealed record SttLanguageOption(string Label, string Code);
