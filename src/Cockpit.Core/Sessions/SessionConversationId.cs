namespace Cockpit.Core.Sessions;

// A session's conversation id, as much as the host currently knows it (AC-408) — the core's own mirror of the
// plugin SDK's `PluginConversationId`. `ISessionConversationSink` takes this rather than the
// plugin type because Cockpit.Core has no reference to Cockpit.Plugins.Abstractions; the adapters in
// Cockpit.Infrastructure, which reference both, map one to the other at the seam.
//
// `State`: Which of the three states this record currently represents.
// `Value`: The provider's id, set only when `State` is `SessionConversationIdState.Known`.
public sealed record SessionConversationId(SessionConversationIdState State, string? Value)
{
    // Not yet known.
    public static SessionConversationId Unknown { get; } = new(SessionConversationIdState.Unknown, null);

    // This provider has no resumable conversation id.
    public static SessionConversationId Unsupported { get; } = new(SessionConversationIdState.Unsupported, null);

    // The provider's real conversation id.
    public static SessionConversationId Known(string value) => new(SessionConversationIdState.Known, value);
}
