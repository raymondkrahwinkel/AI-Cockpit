namespace Cockpit.Core.Sessions;

/// <summary>
/// A session's conversation id, as much as the host currently knows it (AC-408) — the core's own mirror of the
/// plugin SDK's <c>PluginConversationId</c>. <see cref="ISessionConversationSink"/> takes this rather than the
/// plugin type because Cockpit.Core has no reference to Cockpit.Plugins.Abstractions; the adapters in
/// Cockpit.Infrastructure, which reference both, map one to the other at the seam.
/// </summary>
/// <param name="State">Which of the three states this record currently represents.</param>
/// <param name="Value">The provider's id, set only when <paramref name="State"/> is <see cref="SessionConversationIdState.Known"/>.</param>
public sealed record SessionConversationId(SessionConversationIdState State, string? Value)
{
    /// <summary>Not yet known.</summary>
    public static SessionConversationId Unknown { get; } = new(SessionConversationIdState.Unknown, null);

    /// <summary>This provider has no resumable conversation id.</summary>
    public static SessionConversationId Unsupported { get; } = new(SessionConversationIdState.Unsupported, null);

    /// <summary>The provider's real conversation id.</summary>
    public static SessionConversationId Known(string value) => new(SessionConversationIdState.Known, value);
}
