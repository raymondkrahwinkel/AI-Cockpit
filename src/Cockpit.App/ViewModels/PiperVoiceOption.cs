namespace Cockpit.App.ViewModels;

/// <summary>A selectable Piper voice for read-aloud (#35): display label plus the sherpa-onnx voice id (also the model archive name).</summary>
public sealed record PiperVoiceOption(string Label, string VoiceId);
