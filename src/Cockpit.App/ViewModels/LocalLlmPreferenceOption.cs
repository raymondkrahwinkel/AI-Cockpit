using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable local-LLM server preference: display label plus the <see cref="LocalLlmPreference"/> value.</summary>
public sealed record LocalLlmPreferenceOption(string Label, LocalLlmPreference Value);
