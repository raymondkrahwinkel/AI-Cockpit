namespace Cockpit.Core.Abstractions.Agents;

/// <summary>
/// Operator-facing signal for AC-439: which panes currently hold a claim on the same <em>physical</em> resource as a
/// pane on a different workspace. AC-393 partitions claims per desk on purpose — an agent must never see, address or
/// learn about a claim outside its own workspace — so two agents on different desks that reach for the same
/// worktree both succeed and neither is told. That is accepted as the price of the partition, but it leaves the
/// operator, who watches every desk at once, unable to see the collision either. This is the seam that fixes that
/// without touching the partition: it reads across every desk (through <see cref="IAgentResourceClaimsAudit"/>,
/// which no agent-facing tool is given) and reports back to the cockpit UI, never to an agent's tool result.
/// <para>
/// <strong>Not distinguished by severity.</strong> A pane either has a collision or it does not — not "how many
/// neighbours" or "which resource" (Raymond, AC-439: every collision is the same signal in phase 1). The chip this
/// drives is on or off; a more informative signal is future scope, not something to grow here implicitly.
/// </para>
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
