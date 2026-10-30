using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public class SwarmPropagationTests
{
    public static TheoryData<string> Scenarios => new() { "catchup", "forged-record", "wrong-signer", "wrong-pin", "external", "collision", "sleep", "gap" };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task PeersPropagateOnlyAcceptedRecordsAndSelfSignedPinnedUpdates(string scenario)
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        await using var third = await SwarmJoinFixture.Machine.CreateAsync(pair.Time, SwarmHostKind.Desktop);
        await JoinAsync(pair.A, pair.B);
        pair.B.RejectConnect = scenario is "forged-record" or "collision" or "gap";
        await JoinAsync(pair.A, third);
        pair.Time.Advance(TimeSpan.FromSeconds(1));
        if (scenario == "catchup")
        {
            pair.B.RejectConnect = true;
            await using var fourth = await SwarmJoinFixture.Machine.CreateAsync(pair.Time, SwarmHostKind.Desktop);
            await JoinAsync(pair.A, fourth);
            var before = await third.Membership.LoadAsync();
            await third.Propagation.ApplyAsync(await pair.A.Propagation.RecordsAsync(before.Sequence,
                SwarmPropagation.Prefix(before.Log), default), default);
            Assert.Equal(4, (await third.Membership.LoadAsync()).Sequence);
            await pair.A.StopAsync();
            await pair.B.Heartbeat.TickAsync();
            Assert.Contains((await pair.B.Membership.LoadAsync()).Members, member => member.Id == fourth.Identity.MemberId);
            Assert.Contains(pair.B.Notices, notice => notice.Contains("joined the swarm, added by", StringComparison.Ordinal));
            return;
        }
        var membership = await pair.B.Membership.LoadAsync();
        if (scenario is "forged-record" or "collision" or "gap")
        {
            var delta = await third.Propagation.RecordsAsync(membership.Sequence, SwarmPropagation.Prefix(membership.Log), default);
            if (scenario == "forged-record")
                delta = delta with { Records = [delta.Records[0] with { Signatures = [] }] };
            else if (scenario == "gap")
            {
                var proposed = delta.Records[0].Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options) as SwarmMember;
                var gap = pair.A.Identity.Sign(SwarmMembership.ToPayload(proposed! with { Sequence = proposed.Sequence + 1 }));
                using var key = third.Certificate.Value.GetECDsaPrivateKey()!;
                delta = delta with { Records = [gap.CoSign(key)] };
            }
            else
            {
                var competing = delta.Records[0].Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options) as SwarmMember;
                var collision = pair.A.Identity.Sign(SwarmMembership.ToPayload(competing! with { Name = "other" }));
                using var key = third.Certificate.Value.GetECDsaPrivateKey()!;
                Assert.Equal(SwarmMembershipVerdict.Accepted, membership.Accept(collision.CoSign(key)));
                pair.B.Membership.Save(membership);
                await Assert.ThrowsAsync<InvalidDataException>(() => third.Propagation.RecordsAsync(membership.Sequence,
                    SwarmPropagation.Prefix(membership.Log), default));
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => pair.B.Propagation.ApplyAsync(delta, default));
            Assert.Equal(membership.Sequence, (await pair.B.Membership.LoadAsync()).Sequence);
            return;
        }
        var record = await pair.B.Heartbeat.OwnAsync(scenario == "sleep", default);
        if (scenario == "external")
        {
            var update = record.Payload.Deserialize<SwarmHeartbeatUpdate>(SwarmRecordJson.Options)!;
            record = pair.B.Identity.Sign(JsonSerializer.SerializeToElement(update with { Addresses = ["8.8.8.8"] }, SwarmRecordJson.Options));
        }
        if (scenario == "wrong-signer") record = pair.A.Identity.Sign(record.Payload);
        var accepted = await pair.A.Heartbeat.AcceptAsync(record, pair.B.Identity.MemberId,
            scenario == "wrong-pin" ? third.Certificate.Fingerprint : pair.B.Certificate.Fingerprint, default);
        if (scenario is "wrong-signer" or "wrong-pin") Assert.Null(accepted);
        else
        {
            Assert.NotNull(accepted);
            if (scenario == "external") Assert.DoesNotContain("8.8.8.8", pair.A.Heartbeat.Latest(pair.B.Identity.MemberId)!.Addresses);
            Assert.Equal(scenario == "sleep" ? SwarmPresence.Asleep : SwarmPresence.Online, pair.A.Reachability.Get(pair.B.Identity.MemberId).Status);
            Assert.Null(await pair.A.Heartbeat.AcceptAsync(record, pair.B.Identity.MemberId, pair.B.Certificate.Fingerprint, default));
        }
    }

    private static async Task JoinAsync(SwarmJoinFixture.Machine source, SwarmJoinFixture.Machine target)
    {
        var handshake = await source.Client.BeginAsync(target.Address);
        target.Join.Confirm(handshake.Offer.PairingId);
        await source.Client.CompleteAsync(handshake);
    }
}
