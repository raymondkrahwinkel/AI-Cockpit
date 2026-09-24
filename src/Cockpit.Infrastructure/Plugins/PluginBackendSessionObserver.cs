using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// AC-1392: `ICockpitHost.Sessions` for a backend, on ISessionRegistry: the open sessions and which one closed. It has
// no selection (Active* stay empty, D6), and output, tool activity and turn images are never raised or returned,
// because a session handle does not carry them yet (AC-1415). The desktop's own observer has all of them.
public sealed class PluginBackendSessionObserver : ICockpitSessionObserver
{
    private readonly ISessionRegistry _registry;
    private readonly Lock _gate = new();
    private HashSet<string> _live = [];

    // Subscribed before the first read, so no change after that read goes unseen.
    public PluginBackendSessionObserver(ISessionRegistry registry)
    {
        _registry = registry;
        _registry.Changed += _OnRegistryChanged;
        lock (_gate)
        {
            _live = _LivePaneIds();
        }
    }

    public string? ActiveSessionWorkingDirectory => null;

    public IReadOnlyList<OpenCockpitSession> OpenSessions =>
        [.. _registry.All.Select(session => new OpenCockpitSession(session.PaneId, session.Title)),
            .. _registry.Assistant is { } assistant
                ? [new OpenCockpitSession(assistant.PaneId, assistant.Title)]
                : Array.Empty<OpenCockpitSession>()];

    public event EventHandler? ActiveSessionChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<SessionOutputText>? OutputProduced
    {
        add { }
        remove { }
    }

    public event EventHandler<string>? SessionClosed;

    // Changed says only that something moved; the panes that were live before and are not now are the ones that
    // closed. Read and swapped under the gate, so two changes on two threads are compared in the order they read.
    private void _OnRegistryChanged(object? sender, EventArgs e)
    {
        List<string> closed;
        lock (_gate)
        {
            var now = _LivePaneIds();
            closed = [.. _live.Where(paneId => !now.Contains(paneId))];
            _live = now;
        }

        foreach (var paneId in closed)
        {
            SessionClosed?.Invoke(this, paneId);
        }
    }

    private HashSet<string> _LivePaneIds() => new(_registry.All.Select(session => session.PaneId), StringComparer.Ordinal);
}
