namespace Cockpit.App.Plugins;

// Collects the plugins that failed to load or initialize (#14), and (AC-208) the ones sitting at
// awaiting-approval, so the app can keep running while still telling the operator: a startup banner and the
// plugin manager both read this. Written by the `PluginManager` across both phases (some run
// before the DI container exists), so it is created in `Program.Main` and shared, not resolved.
// Thread-safe for the rare concurrent write.
public sealed class PluginDiagnostics
{
    // Phases a plugin never became operative from (#184) — as opposed to a phase recorded afterwards (e.g.
    // `"mcp-server"`, `"compatibility"`), which leaves it loaded but flags one contribution. The one
    // place this is named, so `ForFolder` and a row's own reading of `AllForFolder`
    // classify a phase the same way.
    public static readonly IReadOnlySet<string> ActivationPhases =
        new HashSet<string>(["load", "configure", "initialize"], StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly List<PluginFailure> _failures = [];
    private readonly List<PluginPendingApproval> _pendingApprovals = [];

    // Raised after a new failure or pending-approval is recorded (#184) — the startup banner subscribes so it re-reads instead of only reflecting the snapshot at startup, since a contribution can now fail after that point (e.g. a plugin's fire-and-forget `CockpitHost.AddMcpServer`).
    public event Action? Changed;

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

    // Plugins awaiting the operator's approval (#14/AC-208) — new, or their bytes changed since last approved. A parallel list to `Failures` rather than a third severity: this is not a failure, it is an everyday state the operator clears from the Plugin store.
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

        Changed?.Invoke();
    }

    // Records a plugin as awaiting approval (AC-208) so the startup banner and the plugin-store badge can count it.
    public void RecordPendingApproval(string folderId, string displayName)
    {
        lock (_gate)
        {
            _pendingApprovals.Add(new PluginPendingApproval(folderId, displayName));
        }

        Changed?.Invoke();
    }

    // The failure that best describes a plugin folder's current state, if any — used by the manager to mark
    // the row. A folder can accumulate more than one entry (#184): e.g. a compatibility warning at load time,
    // then a runtime failure from a contribution recorded later (`CockpitHost.AddMcpServer`). An
    // `ActivationPhases` entry always wins over a later contribution one — a plugin that never
    // became operative is the more fundamental fact, even if a fire-and-forget contribution call happens to
    // record its own (lesser) failure afterwards. Among entries of the same standing, the most recent wins.
    public PluginFailure? ForFolder(string folderId)
    {
        lock (_gate)
        {
            var forFolder = _failures.Where(failure => failure.FolderId == folderId).ToList();
            return forFolder.LastOrDefault(failure => ActivationPhases.Contains(failure.Phase))
                ?? forFolder.LastOrDefault();
        }
    }

    // Every failure recorded for a plugin folder, oldest first (#184) — for a consumer that needs to read more than one independent fact out of the history (e.g. "did it ever load" separately from "did its MCP contribution fail"), which a single `ForFolder` result cannot carry at once.
    public IReadOnlyList<PluginFailure> AllForFolder(string folderId)
    {
        lock (_gate)
        {
            return _failures.Where(failure => failure.FolderId == folderId).ToList();
        }
    }
}
