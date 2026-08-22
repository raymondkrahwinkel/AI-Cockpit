namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Operator-facing signal for AC-439: which panes hold a claim on the same <em>physical</em> resource as a pane on
/// a different workspace. AC-393 partitions claims per desk on purpose, leaving even the operator unable to see a
/// cross-desk collision; this reads across every desk (via <see cref="IAgentResourceClaimsAudit"/>, never given to
/// agent-facing tools) to the cockpit UI only. Not distinguished by severity (Raymond, AC-439): a pane either has a collision or not.
/// </summary>
public interface IClaimCollisionMonitor
{
    /// <summary>
    /// Recomputes against the claims and pane/workspace mapping as they stand right now, and returns every pane id
    /// presently in a collision. Pull rather than push — the caller (a UI timer) decides how often "right now" is
    /// asked for, and the answer is never cached, so a claim released between two calls simply stops appearing.
    /// </summary>
    IReadOnlySet<string> PanesInCollision();
}
