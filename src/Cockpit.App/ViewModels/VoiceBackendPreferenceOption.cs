using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable Whisper backend preference: display label plus the <see cref="VoiceBackendPreference"/> value.</summary>
public sealed record VoiceBackendPreferenceOption(string Label, VoiceBackendPreference Value);
