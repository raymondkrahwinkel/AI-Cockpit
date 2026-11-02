using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Sessions;

// Which checkout each session is working in, by pane.
//
// The backend session observer is the primary lookup. This remains as a fallback for older hosts and as the UI
// part's live cache while a session reports working-directory changes.
internal sealed class SessionCheckouts
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IPluginSessionContext> _byPane = new(StringComparer.Ordinal);

    // Remembers a session, keyed by its pane. Called once per session panel; a pane id that is already known is
    // replaced rather than added, so a session panel rebuilt for the same pane leaves one entry, not two.
    public void Remember(IPluginSessionContext session)
    {
        if (session.PaneId is not { Length: > 0 } paneId)
        {
            return;
        }

        lock (_gate)
        {
            _byPane[paneId] = session;
        }
    }

    // Drops a pane on session close. Without it, `Remember` only ever grows `_byPane` — and each entry pins a
    // session's context (and through it the whole session) for the life of the app. Called from the plugin's
    // `ICockpitSessionObserver.SessionClosed` hook. A pane that was never remembered is a no-op.
    public void Forget(string paneId)
    {
        lock (_gate)
        {
            _byPane.Remove(paneId);
        }
    }

    // The checkout that pane is working in, or null when the pane is unknown or has not said yet. Read live from
    // the session rather than from a copy taken when it was remembered: a session's working directory arrives
    // after the panel is built, so a snapshot would be null for the whole life of the session.
    public string? CheckoutFor(string paneId)
    {
        IPluginSessionContext? session;
        lock (_gate)
        {
            session = _byPane.GetValueOrDefault(paneId);
        }

        return session?.WorkingDirectory is { Length: > 0 } directory ? directory : null;
    }

    public bool Knows(string paneId)
    {
        lock (_gate)
        {
            return _byPane.ContainsKey(paneId);
        }
    }
}
