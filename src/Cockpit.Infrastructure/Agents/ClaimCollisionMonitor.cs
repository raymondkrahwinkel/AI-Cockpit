using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete <see cref="IClaimCollisionMonitor"/> behind AC-439: reads every standing claim
/// (<see cref="IAgentResourceClaimsAudit"/>, the one unpartitioned view of the store) and every live pane's
/// workspace (<see cref="IPaneWorkspaceDirectory"/>), groups claims by <see cref="PhysicalResourceIdentity"/>, and
/// reports every pane whose group spans more than one workspace.
/// <para>
/// A collision <em>within</em> one desk never appears here — two claims on the same canonicalized resource with
/// owners in the same workspace either are the same claim, or <see cref="IAgentResourceClaims.Claim"/> already
/// refused the second one (AC-393's own exact-match check, ahead of collision detection). Only two desks reaching
/// for what canonicalizes to one physical resource produces a group spanning more than one workspace, which is
/// exactly the gap AC-439 exists to close.
/// </para>
/// <para>
/// A claim whose owner pane is not in <see cref="IPaneWorkspaceDirectory"/> — a session that has already closed,
/// racing with its own <c>Forget</c> — is left out of every group rather than treated as its own workspace: an
/// unknown pane is not evidence of a second desk.
/// </para>
/// </summary>
internal sealed class ClaimCollisionMonitor(IAgentResourceClaimsAudit claimsAudit, IPaneWorkspaceDirectory paneWorkspaces)
    : IClaimCollisionMonitor, ISingletonService
{
    public IReadOnlySet<string> PanesInCollision()
    {
        var workspaceIdsByPane = paneWorkspaces.WorkspaceIdsByPane();

        return claimsAudit.ListAll()
            .Where(claim => workspaceIdsByPane.ContainsKey(claim.OwnerPaneId))
            .GroupBy(claim => PhysicalResourceIdentity.Canonicalize(claim.Resource), StringComparer.Ordinal)
            .Where(group => group.Select(claim => workspaceIdsByPane[claim.OwnerPaneId]).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(claim => claim.OwnerPaneId))
            .ToHashSet(StringComparer.Ordinal);
    }
}
