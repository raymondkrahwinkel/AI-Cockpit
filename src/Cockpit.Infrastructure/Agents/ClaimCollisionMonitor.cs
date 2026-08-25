using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: AC-439 monitor groups claims by `PhysicalResourceIdentity` and reports panes whose group
// spans more than one workspace (same-desk collisions are already refused by AC-393). A pane not in
// `IPaneWorkspaceDirectory` (closed mid-Forget) is dropped rather than counted as its own workspace.
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
