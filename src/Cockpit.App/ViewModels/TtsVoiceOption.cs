namespace Cockpit.App.ViewModels;

// A selectable read-aloud voice (#35): a display label plus the SupertonicTTS speaker id (sid) it maps to.
public sealed record TtsVoiceOption(string Label, int Sid);
