using System.Net;
using System.Security.Cryptography;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;
using System.Security.Cryptography.X509Certificates;

namespace Cockpit.Core.Tests.Swarm;

public class SwarmLeaderElectionTests
{
    [Fact]
    public async Task ReachableRankAndStableReturnDetermineLeadership()
    {
        foreach (var criterion in new[] { 1, 2, 3 })
        {
            VerifyLeadershipCriterion(criterion);
        }
        await VerifyConcurrentClaimsAsync();
    }

    private static async Task VerifyConcurrentClaimsAsync()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("COCKPIT_SCRATCH") ?? Path.GetTempPath(),
            "swarm-claims-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var certificate = new NodeSelfSignedCertificate(Path.Combine(root, "leader.pfx"));
            var identity = new SwarmIdentity(certificate);
            using var key = certificate.Value.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Missing key.");
            using var laptopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var tabletKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leader = new SwarmMember(key.ExportSubjectPublicKeyInfo(), "Leader", 17, [], 20382,
                identity.MemberId, DateTimeOffset.UnixEpoch, 1);
            var laptop = new SwarmMember(laptopKey.ExportSubjectPublicKeyInfo(), "Laptop", 42, [], 20383,
                leader.Id, DateTimeOffset.UnixEpoch, 2);
            var tablet = new SwarmMember(tabletKey.ExportSubjectPublicKeyInfo(), "Tablet", 55, [], 20384,
                leader.Id, DateTimeOffset.UnixEpoch, 3);
            var register = new SwarmMembership();
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(leader), key)));
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(laptop), key).CoSign(laptopKey)));
            Assert.Equal(SwarmMembershipVerdict.Accepted, register.Accept(SwarmRecord.Sign(SwarmMembership.ToPayload(tablet), key).CoSign(tabletKey)));
            var store = new SwarmMembershipStore(Path.Combine(root, "members.json"));
            store.Save(register);
            var settings = new SwarmSettingsStore(Path.Combine(root, "cockpit.json"));
            await settings.SaveAsync(new() { WhenALowerRankComesBack = SwarmLowerRankReturn.JoinAtBack });
            var clock = new FakeTimeProvider();
            var reachability = new SwarmReachability(clock);
            var leadership = new SwarmLeadership(store, settings, identity, reachability) { Time = clock };
            await leadership.RefreshAsync();
            reachability.Answer(laptop.Id, HttpStatusCode.OK);
            reachability.Answer(tablet.Id, HttpStatusCode.OK);
            var laptopClaim = SwarmRecord.Sign(SwarmMembership.ToPayload(SwarmLeaderElection.ClaimLead(register, laptop.Id)), laptopKey);
            var tabletClaim = SwarmRecord.Sign(SwarmMembership.ToPayload(SwarmLeaderElection.ClaimLead(register, tablet.Id)), tabletKey);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<SwarmRecords?> ClaimAsync(SwarmMemberId caller, SwarmRecord record)
            {
                await start.Task;
                try
                {
                    return await leadership.AcceptClaimAsync(caller, record, default);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }
            var first = Task.Run(() => ClaimAsync(laptop.Id, laptopClaim));
            var second = Task.Run(() => ClaimAsync(tablet.Id, tabletClaim));
            start.SetResult();
            var results = await Task.WhenAll(first, second);
            var accepted = Assert.Single(results, result => result is not null)
                ?? throw new InvalidOperationException("No claim accepted.");
            Assert.Single(results, result => result is null);
            var replay = SwarmMembership.Replay(accepted.Records);
            Assert.Equal(4, replay.Sequence);
            Assert.Equal(replay.Members.MinBy(member => member.Rank)?.Id, leadership.Current?.Id);
            await leadership.RefreshAsync();
            Assert.Equal(replay.ClaimedLeader, leadership.Current?.Id);
            Assert.False(leadership.IsSelf);
            Assert.Equal(replay.ClaimedLeader, (await store.LoadAsync()).ClaimedLeader);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyLeadershipCriterion(int criterion)
    {
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
        {
            Assert.Equal(desktop.Id, Elect(waiting, action: SwarmTakeoverAction.TakeOverNow).Current?.Id);
        }
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
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
