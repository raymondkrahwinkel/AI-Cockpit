namespace Cockpit.App.ViewModels;

/// <summary>A selectable read-aloud voice (#35): a display label plus the SupertonicTTS speaker id (sid) it maps to.</summary>
public sealed record TtsVoiceOption(string Label, int Sid);
