using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.LocalCi.Sessions;

/// <summary>
/// Which checkout each session is working in, by pane.
/// <para>
/// It exists because the host has no lookup for it: a plugin can read the <em>selected</em> session's working
/// directory, and it is handed a session's own context when it builds something into that session's header — but
/// there is nothing that turns an arbitrary pane id into a directory. An MCP tool needs exactly that, because the
/// session that called it is very often not the one the operator is looking at.
/// </para>
/// </summary>
internal sealed class SessionCheckouts
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IPluginSessionContext> _byPane = new(StringComparer.Ordinal);

    /// <summary>
    /// Remembers a session, keyed by its pane. Called once per session panel; a pane id that is already known is
    /// replaced rather than added, so a session panel rebuilt for the same pane leaves one entry, not two.
    /// </summary>
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

    /// <summary>
    /// The checkout that pane is working in, or null when the pane is unknown or has not said yet. Read live from
    /// the session rather than from a copy taken when it was remembered: a session's working directory arrives
    /// after the panel is built, so a snapshot would be null for the whole life of the session.
    /// </summary>
    public string? CheckoutFor(string paneId)
    {
        IPluginSessionContext? session;
        lock (_gate)
        {
            session = _byPane.GetValueOrDefault(paneId);
        }

        return session?.WorkingDirectory is { Length: > 0 } directory ? directory : null;
    }
}
