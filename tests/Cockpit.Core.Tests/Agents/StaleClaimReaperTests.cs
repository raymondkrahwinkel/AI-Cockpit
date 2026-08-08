using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Agents;

// AC-644. The crash net for claims: a liveness check against the live panes, never an age-based expiry.
public class StaleClaimReaperTests
{
    private readonly IAgentResourceClaimsAudit _audit = Substitute.For<IAgentResourceClaimsAudit>();
    private readonly IAgentResourceClaims _claims = Substitute.For<IAgentResourceClaims>();
    private readonly IAgentMessageInbox _inbox = Substitute.For<IAgentMessageInbox>();

    private static AgentResourceClaim _Claim(string resource, string owner, TimeSpan? age = null) =>
        new(resource, owner, DateTimeOffset.UtcNow - (age ?? TimeSpan.Zero));

    private StaleClaimReaper _Reaper(params string[] livePanes) =>
        new(_audit, _claims, _inbox) { LivePaneIds = () => livePanes };

    // Criterion 1: a tick with nothing dead spends nothing — it neither forgets nor says anything.
    [Fact]
    public void ATickWithNoDeadOwnedClaims_CostsNothing()
    {
        _audit.ListAll().Returns([_Claim("D:\\wt\\AC-644", "pane-1")]);

        using var reaper = _Reaper("pane-1");
        reaper.RunOnce();

        _claims.DidNotReceiveWithAnyArgs().Forget(default!);
        _inbox.DidNotReceiveWithAnyArgs().Deliver(default!, default!, default!, default!);
    }

    // Criterion 2: forgotten and reported exactly once, naming both the resource and the pane that is gone.
    [Fact]
    public void AClaimWhoseOwnerDisappeared_IsForgottenAndReportedOnce()
    {
        _audit.ListAll().Returns([_Claim("D:\\wt\\AC-644", "pane-crashed")]);

        using var reaper = _Reaper("pane-1");
        reaper.RunOnce();

        _claims.Received(1).Forget("pane-crashed");
        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            AssistantIdentity.PaneId,
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("pane-crashed") && body.Contains("D:\\wt\\AC-644")));
    }

    // The second tick has nothing left to find, because the first one actually forgot it — a net that reports the
    // same dead pane every quarter of an hour is a net somebody turns off.
    [Fact]
    public void ADeadPane_IsNotReportedAgainOnTheNextTick()
    {
        var standing = new List<AgentResourceClaim> { _Claim("D:\\wt\\AC-644", "pane-crashed") };
        _audit.ListAll().Returns(_ => standing.ToList());
        _claims.When(claims => claims.Forget("pane-crashed")).Do(_ => standing.Clear());

        using var reaper = _Reaper("pane-1");
        reaper.RunOnce();
        reaper.RunOnce();

        _inbox.Received(1).Deliver(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // Criterion 3: liveness, not age. A claim held since this morning by a pane that is still there is not stale.
    [Fact]
    public void AClaimWhoseOwnerIsStillLive_IsNeverTouchedHoweverOldItIs()
    {
        _audit.ListAll().Returns([_Claim("D:\\wt\\AC-644", "pane-1", TimeSpan.FromDays(3))]);

        using var reaper = _Reaper("pane-1");
        reaper.RunOnce();

        _claims.DidNotReceiveWithAnyArgs().Forget(default!);
    }

    // `Forget` drops everything one pane holds, so a pane with three claims is one call and one message — not three
    // reports of which two describe claims that were already gone.
    [Fact]
    public void ADeadPaneHoldingSeveralClaims_IsOneCallAndOneMessage()
    {
        _audit.ListAll().Returns([
            _Claim("D:\\wt\\AC-644", "pane-crashed"),
            _Claim("ac-644-branch", "pane-crashed"),
            _Claim("D:\\wt\\AC-645", "pane-2"),
        ]);

        using var reaper = _Reaper("pane-1");
        reaper.RunOnce();

        _claims.Received(1).Forget("pane-crashed");
        _claims.Received(1).Forget("pane-2");
        _inbox.Received(1).Deliver(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string>(body => body.Contains("D:\\wt\\AC-644") && body.Contains("ac-644-branch")));
    }

    // The assistant holds claims like anyone else — `cockpit-agents` is AlwaysMounted and reaches it — but it is not
    // in `AllSessions()`. `App.axaml.cs` puts it in the live set for exactly this; the wiring is what is asserted.
    [Fact]
    public void TheAssistantsOwnClaims_SurviveWhenItIsInTheLiveSet()
    {
        _audit.ListAll().Returns([_Claim("D:\\wt\\AC-644", AssistantIdentity.PaneId)]);

        using var reaper = _Reaper("pane-1", AssistantIdentity.PaneId);
        reaper.RunOnce();

        _claims.DidNotReceiveWithAnyArgs().Forget(default!);
    }

    [Fact]
    public void WithNothingWiredToSweepAgainst_ItDoesNothing()
    {
        using var reaper = new StaleClaimReaper(_audit, _claims, _inbox);

        reaper.RunOnce();

        _audit.DidNotReceiveWithAnyArgs().ListAll();
    }

    // Asked of the container rather than of the class: an unregistered reaper resolves to null in `App.axaml.cs`,
    // which starts nothing and says nothing — the whole crash net dead with every test still green.
    [Fact]
    public async Task TheContainer_ResolvesTheReaper()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(StaleClaimReaper).Assembly);

        await using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<StaleClaimReaper>());
    }
}
