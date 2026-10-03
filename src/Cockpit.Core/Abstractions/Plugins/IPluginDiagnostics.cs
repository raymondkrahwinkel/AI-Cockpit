using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// How this run's plugins fared (#14, AC-208): the ones that failed and the ones awaiting approval.
/// </summary>
public interface IPluginDiagnostics
{
    /// <summary>
    /// The phases a plugin never became operative from, as opposed to a later phase that flags one contribution (#184).
    /// </summary>
    static readonly IReadOnlySet<string> ActivationPhases = new HashSet<string>(["load", "configure", "initialize"], StringComparer.Ordinal);

    /// <summary>
    /// Raised after a failure or a pending approval is recorded.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// The failures recorded so far.
    /// </summary>
    IReadOnlyList<PluginFailure> Failures { get; }

    /// <summary>
    /// The plugins waiting for the operator's approval.
    /// </summary>
    IReadOnlyList<PluginPendingApproval> PendingApprovals { get; }

    /// <summary>
    /// Whether this run discovered its plugins but loaded none of them (safe mode, AC-478).
    /// </summary>
    bool SafeMode { get; }

    /// <summary>
    /// Records a failure of the plugin in <paramref name="folderId"/> during <paramref name="phase"/>.
    /// </summary>
    void Record(string folderId, string displayName, string phase, string error, PluginIssueSeverity severity = PluginIssueSeverity.Error);
}
