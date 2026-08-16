namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The binding handed back for a pane id no session is running behind (AC-832), and the default
/// <see cref="ICockpitHost.BindToSession"/> returns on a host that predates that seam — an unknown session, an
/// ended one, and an older host all arrive at the same well-behaved object rather than at a null a surface has to
/// branch on.
/// </summary>
public sealed class DetachedSessionBinding(string paneId) : IPluginSessionBinding
{
    public string PaneId => paneId;

    public string? SessionName => null;

    public bool IsLive => false;

    public event EventHandler? Ended
    {
        add { }
        remove { }
    }

    public Task SendAsync(string text) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
