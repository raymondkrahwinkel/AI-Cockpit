namespace Cockpit.App.ViewModels;

/// <summary>
/// A selectable "thinking effort" level: display label, a short value key, and the thinking-token
/// budget (<see cref="MaxThinkingTokens"/>) the session runs with. The budget is applied live via
/// the <c>set_max_thinking_tokens</c> control request — the one budget the control protocol can set
/// mid-session — so higher effort simply means a larger thinking budget. The per-level token counts
/// are Cockpit's own tuning, not a fixed SDK constant.
/// </summary>
public sealed record EffortOption(string Label, string Value, int MaxThinkingTokens);
