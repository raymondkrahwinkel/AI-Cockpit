namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The binding for a pane id no session is running behind (AC-832), and what
/// <see cref="ICockpitHost.BindToSession"/> returns on a host that predates that seam. An unknown id, an ended
/// session and an older host all land here — one not-live object instead of a null to branch on.
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
