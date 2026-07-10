namespace Cockpit.App.Plugins;

/// <summary>
/// Collects the plugins that failed to load or initialize (#14) so the app can keep running while still
/// telling the operator: a startup banner and the plugin manager both read this. Written by the
/// <see cref="PluginManager"/> across both phases (some run before the DI container exists), so it is
/// created in <c>Program.Main</c> and shared, not resolved. Thread-safe for the rare concurrent write.
/// </summary>
public sealed class PluginDiagnostics
{
    private readonly object _gate = new();
    private readonly List<PluginFailure> _failures = [];

    public IReadOnlyList<PluginFailure> Failures
    {
        get
        {
            lock (_gate)
            {
                return _failures.ToList();
            }
        }
    }

    public void Record(string folderId, string displayName, string phase, string error)
    {
        lock (_gate)
        {
            _failures.Add(new PluginFailure(folderId, displayName, phase, error));
        }
    }

    /// <summary>The failure recorded for a plugin folder, if any — used by the manager to mark the row.</summary>
    public PluginFailure? ForFolder(string folderId)
    {
        lock (_gate)
        {
            return _failures.FirstOrDefault(failure => failure.FolderId == folderId);
        }
    }
}
