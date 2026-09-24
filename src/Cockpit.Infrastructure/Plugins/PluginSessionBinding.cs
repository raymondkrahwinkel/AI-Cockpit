using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// The live `IPluginSessionBinding` behind `ICockpitHost.BindToSession` (AC-832). It owns nothing of the session:
// identity and liveness are read from the registry each time rather than cached, so no second view of that pane —
// and nothing that could rival its pty — exists here to go stale. AC-1392: on ISessionRegistry, off any UI thread.
internal sealed class PluginSessionBinding : IPluginSessionBinding
{
    private readonly ISessionRegistry _registry;
    private readonly ICockpitSessionObserver _sessions;
    private readonly Func<string, string, Task> _send;

    public PluginSessionBinding(
        string paneId,
        ISessionRegistry registry,
        ICockpitSessionObserver sessions,
        Func<string, string, Task> send)
    {
        PaneId = paneId;
        _registry = registry;
        _sessions = sessions;
        _send = send;
        _sessions.SessionClosed += _OnSessionClosed;
    }

    public string PaneId { get; }

    public string? SessionName => _registry.Find(PaneId)?.Title;

    public bool IsLive => _registry.Find(PaneId) is not null;

    public event EventHandler? Ended;

    // The send looks the pane up again itself, so a pane that closes after IsLive answered is still left alone.
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
