using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: AC-393 claim store, oldest-first list with the caller's desk applied per call. Locked (check-then-act)
// so two agents can't both claim the same resource on concurrent MCP threads. Flat list, not dict-by-resource,
// because `Forget` and the per-pane cap both look claims up by owner rather than by resource.
internal sealed class AgentResourceClaims : IAgentResourceClaims, IAgentResourceClaimsAudit, ISingletonService
{
    // AC-1013: cap on claims per pane, generous for real use but bounds host memory a looping agent could
    // otherwise grow, which every neighbour's `list_agents` would then pay for in its own context.
    internal const int MaxClaimsPerPane = 50;

    private readonly object _lock = new();

    // Append-ordered, so it is already oldest-first for List and stays that way across removals — no timestamp sort
    // anywhere, and the age a caller reads is the age of the claim it is looking at.
    private readonly List<AgentResourceClaim> _claims = [];

    public AgentClaimResult Claim(string paneId, string resource, IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            if (_Standing(resource, workspacePaneIds) is { } standing)
            {
                return new AgentClaimResult(
                    string.Equals(standing.OwnerPaneId, paneId, StringComparison.Ordinal)
                        ? AgentClaimOutcome.AlreadyHeldByYou
                        : AgentClaimOutcome.HeldByAnother,
                    standing);
            }

            // Counted over every claim this pane holds, not only the ones visible from this desk: the cap is a bound
            // on host memory, and memory does not care which desk the pane was on when it took them.
            if (_claims.Count(claim => string.Equals(claim.OwnerPaneId, paneId, StringComparison.Ordinal)) >= MaxClaimsPerPane)
            {
                return new AgentClaimResult(AgentClaimOutcome.TooManyClaims, null);
            }

            var claimed = new AgentResourceClaim(resource, paneId, DateTimeOffset.UtcNow);
            _claims.Add(claimed);
            return new AgentClaimResult(AgentClaimOutcome.Claimed, claimed);
        }
    }

    public AgentReleaseResult Release(string paneId, string resource, IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            // AC-1013: caller's own claim is checked first so, in the rare window where a desk shows two claims
            // on one name (see Claim), an agent can still release what it holds instead of being told another owns it.
            if (_Standing(resource, workspacePaneIds, ownedBy: paneId) is { } mine)
            {
                _claims.Remove(mine);
                return new AgentReleaseResult(AgentReleaseOutcome.Released, mine);
            }

            return _Standing(resource, workspacePaneIds) is { } theirs
                ? new AgentReleaseResult(AgentReleaseOutcome.HeldByAnother, theirs)
                : new AgentReleaseResult(AgentReleaseOutcome.NotClaimed, null);
        }
    }

    public IReadOnlyList<AgentResourceClaim> List(IReadOnlySet<string> workspacePaneIds)
    {
        lock (_lock)
        {
            // Materialised inside the lock, so the caller never enumerates a list another request thread is adding to.
            return [.. _claims.Where(claim => workspacePaneIds.Contains(claim.OwnerPaneId))];
        }
    }

    public void Forget(string paneId)
    {
        lock (_lock)
        {
            _claims.RemoveAll(claim => string.Equals(claim.OwnerPaneId, paneId, StringComparison.Ordinal));
        }
    }

    // `IAgentResourceClaimsAudit.ListAll` — every claim, from every desk, with no
    // `workspacePaneIds` filter applied. Reachable only through that separate interface (AC-439), never
    // through `IAgentResourceClaims`, so nothing in `AgentsMcpTools` can call it.
    public IReadOnlyList<AgentResourceClaim> ListAll()
    {
        lock (_lock)
        {
            return [.. _claims];
        }
    }

    // AC-1013: claim on `resource` visible to the caller's desk, narrowed by `ownedBy` when given.
    // Compared ordinally — the host can't guess whether a resource string is a path or branch name to
    // normalise, and a pane id is host-minted where two spellings mean two panes.
    private AgentResourceClaim? _Standing(string resource, IReadOnlySet<string> workspacePaneIds, string? ownedBy = null) =>
        _claims.FirstOrDefault(claim =>
            string.Equals(claim.Resource, resource, StringComparison.Ordinal)
            && workspacePaneIds.Contains(claim.OwnerPaneId)
            && (ownedBy is null || string.Equals(claim.OwnerPaneId, ownedBy, StringComparison.Ordinal)));
}
