namespace Cockpit.App.ViewModels;

/// <summary>
/// A selectable "thinking effort" level: display label plus the value sent to the Agent SDK's
/// thinking-budget control (<c>setMaxThinkingTokens</c> / <c>--thinking-budget</c>-equivalent).
/// UNVERIFIED mapping: <see cref="MaxThinkingTokens"/> is a best-guess numeric budget per level
/// (there is no confirmed source for exact token counts per <c>EffortLevel</c> in this sandbox) —
/// treat these three numbers as placeholders to tune once verified against a real session.
/// </summary>
public sealed record EffortOption(string Label, string Value, int MaxThinkingTokens);
