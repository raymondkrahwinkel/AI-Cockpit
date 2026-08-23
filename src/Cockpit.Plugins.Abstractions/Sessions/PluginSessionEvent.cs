namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Base type for every typed event an <see cref="IPluginSessionDriver"/> can raise (#45) — the plugin-facing
/// mirror of <c>Cockpit.Core.Sessions.SessionEvent</c>. It started as a trimmed subset and has grown additively
/// as providers proved they can produce more: a reasoning trace (<see cref="PluginAssistantThinkingDelta"/>),
/// the session's cwd and a turn's token usage (#45 D3). The host's driver adapter maps each subtype to its
/// <c>SessionEvent</c> counterpart so the rest of the app sees one event vocabulary regardless of which driver
/// produced it.
/// </summary>
public abstract record PluginSessionEvent
{
    /// <summary>
    /// Session id the driver assigned, once known.
    /// </summary>
    public required string? SessionId { get; init; }

    /// <summary>
    /// Non-null when this event belongs to a nested Task/sub-agent tool call rather than the top-level
    /// conversation (AC-146) — carried verbatim from the wire event's own parent id, so the host can attribute
    /// a sub-agent's activity to the parent tool-use row instead of flattening it into the top-level transcript.
    /// Optional: a provider with no sub-agent concept leaves it <see langword="null"/>, and an already-compiled
    /// plugin that never sets it keeps constructing every event the old way.
    /// </summary>
    public string? ParentToolUseId { get; init; }
}
