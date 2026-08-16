using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

// The live `IPluginSessionBinding` behind `ICockpitHost.BindToSession` (AC-832): a plugin surface tied to one
// running pane. It owns nothing of that session — identity and liveness are read from the cockpit each time rather
// than cached, the end is heard on the shared observer's `SessionClosed`, and the write-back is the host's own
// `SendToSessionAsync`. So two bindings on one session, or a binding beside the pane the grid already draws, add
// nothing that could rival its pty.
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
