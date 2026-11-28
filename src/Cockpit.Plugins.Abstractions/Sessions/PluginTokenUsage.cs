namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Token counts a provider reports for one completed turn (#45 D3) — the plugin-facing mirror of
/// <c>Cockpit.Core.Sessions.TokenUsage</c>, carried on <see cref="PluginTurnCompleted.Usage"/> so the host's
/// running token/cost meter (#8) can fold it in. How the counts accumulate across turns is the host's concern;
/// this just reports what one turn cost.
/// </summary>
/// <param name="InputTokens">
/// Input (prompt) tokens the cache neither served nor took. The three input counts never overlap: a provider whose
/// own input count includes cached or cache-write tokens (Codex, OpenAI) subtracts them first.
/// </param>
/// <param name="OutputTokens">
/// Output (completion) tokens for the turn, reasoning included.
/// </param>
/// <param name="CacheReadInputTokens">
/// Input tokens served from cache rather than re-read.
/// </param>
/// <param name="CacheCreationInputTokens">
/// Input tokens written to cache this turn.
/// </param>
public sealed record PluginTokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadInputTokens,
    int CacheCreationInputTokens)
{
    /// <summary>
    /// True when the provider reports cache counts, so zero reads is a miss. Left false, a turn with both cache
    /// counts at zero reads as unknown rather than as a 0% hit rate.
    /// </summary>
    public bool CacheReported { get; init; }
}
