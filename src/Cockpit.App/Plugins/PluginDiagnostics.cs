namespace Cockpit.App.Plugins;

/// <summary>
/// Collects the plugins that failed to load or initialize (#14), and (AC-208) the ones sitting at
/// awaiting-approval, so the app can keep running while still telling the operator: a startup banner and the
/// plugin manager both read this. Written by the <see cref="PluginManager"/> across both phases (some run
/// before the DI container exists), so it is created in <c>Program.Main</c> and shared, not resolved.
/// Thread-safe for the rare concurrent write.
/// </summary>
public sealed class PluginDiagnostics
{
    private readonly object _gate = new();
    private readonly List<PluginFailure> _failures = [];
    private readonly List<PluginPendingApproval> _pendingApprovals = [];

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

    /// <summary>Plugins awaiting the operator's approval (#14/AC-208) — new, or their bytes changed since last approved. A parallel list to <see cref="Failures"/> rather than a third severity: this is not a failure, it is an everyday state the operator clears from the Plugin store.</summary>
    public IReadOnlyList<PluginPendingApproval> PendingApprovals
    {
        get
        {
            lock (_gate)
            {
                return _pendingApprovals.ToList();
            }
        }
    }

    public void Record(string folderId, string displayName, string phase, string error, PluginIssueSeverity severity = PluginIssueSeverity.Error)
    {
        lock (_gate)
        {
            _failures.Add(new PluginFailure(folderId, displayName, phase, error, severity));
        }
    }

    /// <summary>Records a plugin as awaiting approval (AC-208) so the startup banner and the plugin-store badge can count it.</summary>
    public void RecordPendingApproval(string folderId, string displayName)
    {
        lock (_gate)
        {
            _pendingApprovals.Add(new PluginPendingApproval(folderId, displayName));
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
