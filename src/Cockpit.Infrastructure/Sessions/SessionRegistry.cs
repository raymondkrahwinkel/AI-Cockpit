using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1373: the live panes as one thread-safe list, fed by whoever owns them (today `CockpitViewModel`).
// No dispatcher: a reader on a request thread gets a snapshot rather than a hop onto the UI thread.
public sealed class SessionRegistry : ISessionRegistry, ISingletonService
{
    private readonly Lock _gate = new();
    private readonly List<ISessionHandle> _handles = [];

    public event EventHandler? Changed;

    public IReadOnlyList<ISessionHandle> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _handles];
            }
        }
    }

    public ISessionHandle? Find(string paneId)
    {
        lock (_gate)
        {
            return _handles.Find(handle => string.Equals(handle.PaneId, paneId, StringComparison.Ordinal));
        }
    }

    // A pane id registered twice replaces the earlier handle, so the registry never answers with two panes for one id.
    public void Register(ISessionHandle handle)
    {
        lock (_gate)
        {
            _handles.RemoveAll(existing => string.Equals(existing.PaneId, handle.PaneId, StringComparison.Ordinal));
            _handles.Add(handle);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unregister(string paneId)
    {
        int removed;
        lock (_gate)
        {
            removed = _handles.RemoveAll(handle => string.Equals(handle.PaneId, paneId, StringComparison.Ordinal));
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
