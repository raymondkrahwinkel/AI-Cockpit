using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// The live `IPluginSessionBinding` behind `ICockpitHost.BindToSession` (AC-832). It owns nothing of the session:
// identity and liveness are read from the cockpit each time rather than cached, so no second view of that pane —
// and nothing that could rival its pty — exists here to go stale.
internal sealed class CockpitSessionBinding : IPluginSessionBinding
{
    private readonly CockpitViewModel _cockpit;
    private readonly ICockpitSessionObserver _sessions;
    private readonly Func<string, string, Task> _send;

    public CockpitSessionBinding(
        string paneId,
        CockpitViewModel cockpit,
        ICockpitSessionObserver sessions,
        Func<string, string, Task> send)
    {
        PaneId = paneId;
        _cockpit = cockpit;
        _sessions = sessions;
        _send = send;
        _sessions.SessionClosed += _OnSessionClosed;
    }

    public string PaneId { get; }

    public string? SessionName => _cockpit.FindSession(PaneId)?.Title;

    public bool IsLive => _cockpit.FindSession(PaneId) is not null;

    public event EventHandler? Ended;

    public Task SendAsync(string text) => IsLive ? _send(PaneId, text) : Task.CompletedTask;

    public void Dispose() => _sessions.SessionClosed -= _OnSessionClosed;

    private void _OnSessionClosed(object? sender, string paneId)
    {
        if (!string.Equals(paneId, PaneId, StringComparison.Ordinal))
        {
            return;
        }

        // Nothing more can arrive for a pane that is gone, so let go of the shared observer here rather than wait
        // for a surface that may never dispose us.
        _sessions.SessionClosed -= _OnSessionClosed;
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
