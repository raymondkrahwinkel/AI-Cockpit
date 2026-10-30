using System.Net;
using System.Security.Cryptography;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

public class SwarmLeaderElectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReachableRankAndStableReturnDetermineLeadership(int criterion)
    {
        switch (criterion)
        {
            case 1:
                SwarmRankTests.CollisionIsRetriedBeforeSigning();
                break;
            case 2:
                SwarmReachabilityTests.OnlyAuthorizedAnswersRenewTheLeaseAndStableReturn(true);
                SwarmReachabilityTests.OnlyAuthorizedAnswersRenewTheLeaseAndStableReturn(false);
                break;
            case 3:
                foreach (var row in SwarmMembershipTests.Forgeries)
                {
                    SwarmMembershipTests.Accept_RefusesAChangeNotSignedByACurrentMember(
                        (Func<ECDsa[], SwarmRecord[]>)row[0], (SwarmMembershipVerdict)row[1]);
                }
                break;
        }
        using var desktopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var laptopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var desktop = new SwarmMember(desktopKey.ExportSubjectPublicKeyInfo(), "Desktop", 17, ["10.0.0.1"], 7443,
            SwarmMemberId.FromPublicKey(desktopKey.ExportSubjectPublicKeyInfo()), DateTimeOffset.UnixEpoch, 1);
        var laptop = new SwarmMember(laptopKey.ExportSubjectPublicKeyInfo(), "Laptop", 42, ["10.0.0.2"], 7443,
            desktop.Id, DateTimeOffset.UnixEpoch, 2);
        var register = new SwarmMembership();
        Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(desktop), desktopKey)));
        Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(laptop), desktopKey).CoSign(laptopKey)));
        var time = new FakeTimeProvider();
        var reachability = new SwarmReachability(time);
        void Answer() { reachability.Answer(desktop.Id, HttpStatusCode.OK); reachability.Answer(laptop.Id, HttpStatusCode.OK); }
        SwarmLeadershipState Elect(SwarmLeadershipState state, SwarmLowerRankReturn mode = SwarmLowerRankReturn.AnnouncedTakeover,
            SwarmTakeoverAction action = SwarmTakeoverAction.Wait) =>
            SwarmLeaderElection.Evaluate(register, reachability.Get, time.GetUtcNow(), state, mode, action);
        Answer();
        var initial = Elect(new());
        Assert.Equal(desktop.Id, initial.Current?.Id);
        time.Advance(10);
        Answer();
        Assert.Equal(desktop.Id, Elect(initial).Current?.Id);
        time.Advance(16);
        reachability.Answer(laptop.Id, HttpStatusCode.OK);
        var alone = Elect(initial);
        Assert.Equal(laptop.Id, alone.Current?.Id);
        Assert.Equal(alone.Current?.Id, Elect(initial).Current?.Id);
        if (criterion == 1)
        {
            var removal = new SwarmTombstone(laptop.Id, desktop.Id, time.GetUtcNow(), register.Sequence + 1);
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(removal), desktopKey)));
            Answer();
            Assert.Equal(17, Elect(new()).Current?.Rank);
            Assert.Equal(17, SwarmMembership.Replay(register.Log).Members.Single().Rank);
            return;
        }
        Answer();
        var mode = criterion == 3 ? SwarmLowerRankReturn.JoinAtBack : SwarmLowerRankReturn.AnnouncedTakeover;
        var waiting = Elect(alone, mode);
        Assert.Equal(laptop.Id, waiting.Current?.Id);
        Assert.Equal(desktop.Id, waiting.Pending?.Member.Id);
        Assert.Equal(time.GetUtcNow().AddSeconds(30), waiting.Pending?.At);
        if (criterion == 2)
            Assert.Equal(desktop.Id, Elect(waiting, action: SwarmTakeoverAction.TakeOverNow).Current?.Id);
        for (var i = 0; i < 5; i++) { time.Advance(5); Answer(); }
        Assert.Equal(laptop.Id, Elect(waiting, mode).Current?.Id);
        time.Advance(5);
        Answer();
        if (criterion == 2)
        {
            var cancelled = Elect(waiting, action: SwarmTakeoverAction.StayWithCurrent);
            Assert.Null(cancelled.Pending);
            Assert.Equal(laptop.Id, Elect(cancelled).Current?.Id);
            var resumed = Elect(cancelled, action: SwarmTakeoverAction.TakeOverNow);
            Assert.Equal(desktop.Id, resumed.Current?.Id);
            Assert.Null(resumed.Suppressed);
            Assert.Null(resumed.Pending);
            Assert.Equal(desktop.Id, Elect(waiting).Current?.Id);
            Assert.Equal(desktop.Id, Elect(waiting, action: SwarmTakeoverAction.TakeOverNow).Current?.Id);
            time.Advance(16);
            reachability.Answer(laptop.Id, HttpStatusCode.OK);
            var absent = Elect(waiting);
            Assert.Null(absent.Pending);
            Answer();
            var again = Elect(absent);
            time.Advance(10);
            Answer();
            Assert.Equal(laptop.Id, Elect(again).Current?.Id);
            using var thirdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var third = new SwarmMember(thirdKey.ExportSubjectPublicKeyInfo(), "Server", 5, ["10.0.0.3"], 7443,
                laptop.Id, time.GetUtcNow(), register.Sequence + 1);
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(third), laptopKey).CoSign(thirdKey)));
            reachability.Answer(third.Id, HttpStatusCode.OK);
            Assert.Equal(third.Id, Elect(again).Pending?.Member.Id);
            return;
        }
        var change = SwarmLeaderElection.RejoinAtBack(register, desktop.Id);
        Assert.True(change.Rank > laptop.Rank);
        Assert.Equal(SwarmMembershipVerdict.MissingSignature, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(change), laptopKey)));
        Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(change), desktopKey)));
        var replay = SwarmMembership.Replay(register.Log);
        Assert.Equal(change.Rank, replay.Members.Single(member => member.Id == desktop.Id).Rank);
        Assert.Equal(laptop.Id, Elect(waiting, mode).Current?.Id);
        Assert.Null(Elect(waiting, mode).Pending);
        Assert.True(SwarmRank.DrawAbove([long.MinValue]) > long.MinValue);
        Assert.Equal(long.MaxValue, SwarmRank.DrawAbove([long.MaxValue - 1]));
        Assert.Throws<InvalidOperationException>(() => SwarmRank.DrawAbove([long.MaxValue]));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
