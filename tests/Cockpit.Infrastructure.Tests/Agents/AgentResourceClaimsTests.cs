using Cockpit.Core.Abstractions.Agents;
using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// The claim store itself (AC-393), independent of the MCP tools that drive it. The workspace partition Raymond
/// settled on is expressed here as the desk the caller passes in — the panes the host says share its workspace — so
/// every test that cares about isolation states two desks explicitly rather than relying on a stored workspace id
/// that would drift out from under a pane (the reason the roster and the inbox key on pane id alone).
/// </summary>
public sealed class AgentResourceClaimsTests
{
    private static IReadOnlySet<string> _Desk(params string[] paneIds) => paneIds.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Claim_WhatNobodyOnTheDeskHolds_IsTaken()
    {
        var claims = new AgentResourceClaims();

        var result = claims.Claim("pane-1", "/repo/worktree-a", _Desk("pane-1", "pane-2"));

        Assert.Equal(AgentClaimOutcome.Claimed, result.Outcome);
        Assert.Equal("/repo/worktree-a", result.Claim?.Resource);
        Assert.Equal("pane-1", result.Claim?.OwnerPaneId);
    }

    /// <summary>AC1 — the second claimer sees the standing claim, and sees whose it is.</summary>
    [Fact]
    public void Claim_WhatANeighbourHolds_IsRefusedAndNamesTheHolder()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "/repo/worktree-a", desk);

        var second = claims.Claim("pane-2", "/repo/worktree-a", desk);

        Assert.Equal(AgentClaimOutcome.HeldByAnother, second.Outcome);
        Assert.Equal("pane-1", second.Claim?.OwnerPaneId);
    }

    /// <summary>
    /// Re-claiming is idempotent and does not renew: an agent that claims in a loop must not keep its resource looking
    /// permanently fresh to a neighbour watching the age for a claim its owner walked away from.
    /// </summary>
    [Fact]
    public void Claim_WhatTheCallerAlreadyHolds_KeepsTheOriginalClaimRatherThanTakingASecond()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1");
        var first = claims.Claim("pane-1", "feature/AC-393", desk);

        var again = claims.Claim("pane-1", "feature/AC-393", desk);

        Assert.Equal(AgentClaimOutcome.AlreadyHeldByYou, again.Outcome);
        Assert.Equal(first.Claim?.ClaimedAtUtc, again.Claim?.ClaimedAtUtc);
        Assert.Single(claims.List(desk));
    }

    /// <summary>
    /// AC4 — the workspace boundary. Two agents on different desks hold the same resource name at the same time and
    /// neither sees the other: the claim is not merely hidden from the list, it does not block, which is what makes
    /// the partition a partition rather than a filtered view of one shared registry.
    /// </summary>
    [Fact]
    public void Claim_TheSameResourceOnTwoDesks_SucceedsOnBothAndNeitherSeesTheOther()
    {
        var claims = new AgentResourceClaims();
        var deskX = _Desk("pane-x");
        var deskY = _Desk("pane-y");
        claims.Claim("pane-x", "/repo/worktree-a", deskX);

        var onY = claims.Claim("pane-y", "/repo/worktree-a", deskY);

        Assert.Equal(AgentClaimOutcome.Claimed, onY.Outcome);
        Assert.Equal("pane-x", Assert.Single(claims.List(deskX)).OwnerPaneId);
        Assert.Equal("pane-y", Assert.Single(claims.List(deskY)).OwnerPaneId);
    }

    [Fact]
    public void Claim_PastThePerPaneCap_IsRefusedAndTakesNothing()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1");
        for (var i = 0; i < AgentResourceClaims.MaxClaimsPerPane; i++)
        {
            Assert.Equal(AgentClaimOutcome.Claimed, claims.Claim("pane-1", $"/repo/worktree-{i}", desk).Outcome);
        }

        var overTheCap = claims.Claim("pane-1", "/repo/one-too-many", desk);

        Assert.Equal(AgentClaimOutcome.TooManyClaims, overTheCap.Outcome);
        Assert.Null(overTheCap.Claim);
        Assert.Equal(AgentResourceClaims.MaxClaimsPerPane, claims.List(desk).Count);
    }

    /// <summary>
    /// The cap bounds host memory, so it counts everything the pane holds — including claims taken while it was on a
    /// desk it is no longer part of, which are invisible to the current caller but still occupy memory.
    /// </summary>
    [Fact]
    public void Claim_CountsTowardsTheCapEvenWhenTheClaimIsNotVisibleFromTheCurrentDesk()
    {
        var claims = new AgentResourceClaims();
        for (var i = 0; i < AgentResourceClaims.MaxClaimsPerPane; i++)
        {
            claims.Claim("pane-1", $"/repo/worktree-{i}", _Desk("pane-1"));
        }

        // A desk that no longer holds pane-1 sees none of those claims, but they are still pane-1's.
        var elsewhere = claims.Claim("pane-1", "/repo/elsewhere", _Desk("pane-2"));

        Assert.Equal(AgentClaimOutcome.TooManyClaims, elsewhere.Outcome);
    }

    [Fact]
    public void Release_ByTheHolder_GivesItUpSoANeighbourCanTakeIt()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "/repo/worktree-a", desk);

        var released = claims.Release("pane-1", "/repo/worktree-a", desk);

        Assert.Equal(AgentReleaseOutcome.Released, released.Outcome);
        Assert.Empty(claims.List(desk));
        Assert.Equal(AgentClaimOutcome.Claimed, claims.Claim("pane-2", "/repo/worktree-a", desk).Outcome);
    }

    /// <summary>AC2 — a claim is only its holder's to give up, or it guarantees nothing to the agent relying on it.</summary>
    [Fact]
    public void Release_ByANeighbourWhoDoesNotHoldIt_IsRefusedAndTheClaimStands()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "/repo/worktree-a", desk);

        var refused = claims.Release("pane-2", "/repo/worktree-a", desk);

        Assert.Equal(AgentReleaseOutcome.HeldByAnother, refused.Outcome);
        Assert.Equal("pane-1", refused.Claim?.OwnerPaneId);
        Assert.Equal("pane-1", Assert.Single(claims.List(desk)).OwnerPaneId);
    }

    [Fact]
    public void Release_WhatNobodyHolds_ReportsNotClaimedRatherThanSuccess()
    {
        var claims = new AgentResourceClaims();

        var result = claims.Release("pane-1", "/repo/never-claimed", _Desk("pane-1"));

        Assert.Equal(AgentReleaseOutcome.NotClaimed, result.Outcome);
        Assert.Null(result.Claim);
    }

    /// <summary>
    /// The isolation boundary holds for release as well as for claim: an agent on another desk cannot reach across it
    /// to drop a claim, and does not even learn that one exists.
    /// </summary>
    [Fact]
    public void Release_AClaimHeldOnAnotherDesk_ReportsNotClaimedAndLeavesItStanding()
    {
        var claims = new AgentResourceClaims();
        claims.Claim("pane-x", "/repo/worktree-a", _Desk("pane-x"));

        var fromTheOtherDesk = claims.Release("pane-y", "/repo/worktree-a", _Desk("pane-y"));

        Assert.Equal(AgentReleaseOutcome.NotClaimed, fromTheOtherDesk.Outcome);
        Assert.Single(claims.List(_Desk("pane-x")));
    }

    /// <summary>
    /// Matched character for character, deliberately: the resource is a string the agents chose, and a host that
    /// case-folded or trimmed separators here would be guessing which of a path, a branch and a file name it holds.
    /// The cost is that neighbours must agree on the spelling, which is what the tool description says.
    /// </summary>
    [Theory]
    [InlineData("/repo/Worktree-A")]
    [InlineData("/repo/worktree-a/")]
    [InlineData("repo/worktree-a")]
    public void Claim_AResourceSpeltDifferently_IsADifferentResource(string spelling)
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "/repo/worktree-a", desk);

        var other = claims.Claim("pane-2", spelling, desk);

        Assert.Equal(AgentClaimOutcome.Claimed, other.Outcome);
    }

    [Fact]
    public void List_ReturnsOnlyTheDesksOwnClaims_OldestFirst()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "first", desk);
        claims.Claim("pane-2", "second", desk);
        claims.Claim("pane-elsewhere", "third", _Desk("pane-elsewhere"));

        var listed = claims.List(desk);

        Assert.Equal(["first", "second"], listed.Select(claim => claim.Resource));
    }

    /// <summary>AC3 — a claim does not outlive the pane that took it; this is the whole of the expiry story in phase 1.</summary>
    [Fact]
    public void Forget_DropsEveryClaimThatPaneHeld_AndLeavesTheOthersAlone()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-1", "/repo/worktree-a", desk);
        claims.Claim("pane-1", "feature/AC-393", desk);
        claims.Claim("pane-2", "/repo/worktree-b", desk);

        claims.Forget("pane-1");

        var left = claims.List(desk);
        Assert.Equal("/repo/worktree-b", Assert.Single(left).Resource);
    }

    [Fact]
    public void Forget_APaneHoldingNothing_IsANoOp()
    {
        var claims = new AgentResourceClaims();
        var desk = _Desk("pane-1", "pane-2");
        claims.Claim("pane-2", "/repo/worktree-b", desk);

        claims.Forget("pane-1");

        Assert.Single(claims.List(desk));
    }

    /// <summary>
    /// The reason everything here is behind one lock rather than a concurrent dictionary: claiming is a check-then-act,
    /// and two agents reaching for the same worktree on two MCP request threads is exactly the collision this feature
    /// exists to prevent — so it must not be the case that both are told they have it.
    /// </summary>
    [Fact]
    public async Task Claim_TheSameResourceFromManyThreadsAtOnce_GrantsItExactlyOnce()
    {
        var claims = new AgentResourceClaims();
        const int contenders = 32;
        var paneIds = Enumerable.Range(0, contenders).Select(i => $"pane-{i}").ToArray();
        var desk = paneIds.ToHashSet(StringComparer.Ordinal);

        var outcomes = await Task.WhenAll(paneIds.Select(paneId =>
            Task.Run(() => claims.Claim(paneId, "/repo/worktree-a", desk).Outcome)));

        Assert.Equal(1, outcomes.Count(outcome => outcome == AgentClaimOutcome.Claimed));
        Assert.Equal(contenders - 1, outcomes.Count(outcome => outcome == AgentClaimOutcome.HeldByAnother));
        Assert.Single(claims.List(desk));
    }
}
