using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// The concrete `IClaimCollisionMonitor` behind AC-439: reads every standing claim
// (`IAgentResourceClaimsAudit`, the one unpartitioned view of the store) and every live pane's
// workspace (`IPaneWorkspaceDirectory`), groups claims by `PhysicalResourceIdentity`, and
// reports every pane whose group spans more than one workspace.
//
// A collision *within* one desk never appears here — two claims on the same canonicalized resource with
// owners in the same workspace either are the same claim, or `IAgentResourceClaims.Claim` already
// refused the second one (AC-393's own exact-match check, ahead of collision detection). Only two desks reaching
// for what canonicalizes to one physical resource produces a group spanning more than one workspace, which is
// exactly the gap AC-439 exists to close.
//
// A claim whose owner pane is not in `IPaneWorkspaceDirectory` — a session that has already closed,
// racing with its own `Forget` — is left out of every group rather than treated as its own workspace: an
// unknown pane is not evidence of a second desk.
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
