namespace Cockpit.Core.Sessions;

// Whether a session's conversation id is known yet, and if not, why (AC-408) — the core's own mirror of the
// plugin SDK's `PluginConversationIdState`, kept as an independent copy so Cockpit.Core never has to
// reference the plugin contract assembly (it stays plugin-agnostic; see `ISessionConversationSink`).
public enum SessionConversationIdState
{
    // Not yet known — the session has not reported one.
    Unknown,

    // This provider has no resumable conversation id at all — a fact, not a failure.
    Unsupported,

    // The id is known — see `SessionConversationId.Value`.
    Known,
}
