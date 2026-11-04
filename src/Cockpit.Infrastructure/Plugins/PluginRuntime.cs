using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Plugins;

// AC-2024: one loaded plugin's contributions as undo actions, recorded by its hosts at each Add*.
public sealed class PluginRuntime(string pluginId, ILogger? logger = null) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Action> _undo = [];
    private bool _disposed;

    public string PluginId => pluginId;

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public void Add(IDisposable undo) => Add(undo.Dispose);

    // A plugin's late async registration lands after the runtime is gone, and is undone on the spot.
    public void Add(Action undo)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _undo.Add(undo);
                return;
            }
        }

        _Run(undo);
    }

    // Last in, first out, so a contribution leaves before what it was built on.
    public void Dispose()
    {
        Action[] undo;
        lock (_gate)
        {
            _disposed = true;
            undo = [.. _undo];
            _undo.Clear();
        }

        foreach (var action in undo.Reverse())
        {
            _Run(action);
        }
    }

    private void _Run(Action undo)
    {
        try
        {
            undo();
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Undoing a contribution of plugin {PluginId} failed; the others still go.", pluginId);
        }
    }
}
