namespace Cockpit.Core.Sessions;

/// <summary>
/// Whether a session's conversation id is known yet, and if not, why (AC-408) — the core's own mirror of the
/// plugin SDK's <c>PluginConversationIdState</c>, kept as an independent copy so Cockpit.Core never has to
/// reference the plugin contract assembly (it stays plugin-agnostic; see <see cref="ISessionConversationSink"/>).
/// </summary>
public enum SessionConversationIdState
{
    /// <summary>Not yet known — the session has not reported one.</summary>
    Unknown,

    /// <summary>This provider has no resumable conversation id at all — a fact, not a failure.</summary>
    Unsupported,

    /// <summary>The id is known — see <see cref="SessionConversationId.Value"/>.</summary>
    Known,
}
