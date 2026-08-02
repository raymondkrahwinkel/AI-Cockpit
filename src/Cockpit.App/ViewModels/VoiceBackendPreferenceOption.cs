using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

// A selectable Whisper backend preference: display label plus the `VoiceBackendPreference` value.
public sealed record VoiceBackendPreferenceOption(string Label, VoiceBackendPreference Value);
