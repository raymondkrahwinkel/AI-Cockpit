namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Base type for every typed event an <see cref="IPluginSessionDriver"/> can raise (#45) — the plugin-facing
/// mirror of <c>Cockpit.Core.Sessions.SessionEvent</c>.
/// </summary>
/// <remarks>
/// The host's driver adapter maps each subtype to its <c>SessionEvent</c> counterpart so the rest of the app sees
/// one event vocabulary regardless of which driver produced it.
/// </remarks>
public abstract record PluginSessionEvent
{
    /// <summary>
    /// Session id the driver assigned, once known.
    /// </summary>
    public required string? SessionId { get; init; }

    /// <summary>
    /// Non-null when this event belongs to a nested Task/sub-agent tool call rather than the top-level
    /// conversation (AC-146) — carried verbatim from the wire event's own parent id.
    /// </summary>
    /// <remarks>
    /// A provider with no sub-agent concept leaves it <see langword="null"/>.
    /// </remarks>
    public string? ParentToolUseId { get; init; }
}
