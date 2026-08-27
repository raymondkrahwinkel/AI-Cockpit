namespace Cockpit.App.ViewModels;

// A selectable "thinking effort" level: display label, a short value key, and the thinking-token budget
// (`MaxThinkingTokens`) the session runs with.
public sealed record EffortOption(string Label, string Value, int MaxThinkingTokens);
