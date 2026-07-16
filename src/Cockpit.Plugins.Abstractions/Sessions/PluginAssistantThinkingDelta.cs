namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// An extended-thinking (reasoning) chunk, streamed separately from visible assistant text so the host can
/// render it collapsed/dimmed (#45 D3) — the plugin-facing mirror of <c>Cockpit.Core.Sessions.AssistantThinkingDelta</c>.
/// A provider that streams a reasoning trace (Codex's <c>item/reasoning/textDelta</c>) raises this; one that
/// does not simply never emits it.
/// </summary>
public sealed record PluginAssistantThinkingDelta : PluginSessionEvent
{
    /// <summary>Index of the thinking block this chunk belongs to, so successive chunks accumulate into one block.</summary>
    public required int BlockIndex { get; init; }

    /// <summary>The reasoning text of this chunk.</summary>
    public required string Thinking { get; init; }
}
