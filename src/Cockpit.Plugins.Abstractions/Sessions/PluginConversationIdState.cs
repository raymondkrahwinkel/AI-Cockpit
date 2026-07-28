namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>Whether a provider's conversation id is known yet, and if not, why (AC-408).</summary>
public enum PluginConversationIdState
{
    /// <summary>Not yet known — the session has not reported one.</summary>
    Unknown,

    /// <summary>This provider has no resumable conversation id at all — a fact, not a failure.</summary>
    Unsupported,

    /// <summary>The id is known — see <see cref="PluginConversationId.Value"/>.</summary>
    Known,
}
