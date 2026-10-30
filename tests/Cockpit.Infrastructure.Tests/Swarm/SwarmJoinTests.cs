using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public class SwarmJoinTests
{
    public static TheoryData<string> Outcomes => new() { "confirmed", "refused", "expired", "pending" };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task JoinRequiresBothOperatorsAndGrantsBothDirections(string outcome)
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        var handshake = await pair.A.Client.BeginAsync(pair.B.Address);
        Assert.Equal(pair.B.Join.Pending!.Code, handshake.Code);
        Assert.Equal(6, handshake.Code.Length);
        Assert.Equal(pair.B.Identity.MemberId, SwarmMemberId.FromPublicKey(handshake.PublicKey));
        Assert.NotEqual(pair.B.Certificate.Fingerprint, pair.B.Identity.MemberId.Value);
        if (outcome == "confirmed")
        {
            pair.B.Join.Confirm(handshake.Offer.PairingId);
            await pair.A.Client.CompleteAsync(handshake);
            var a = await pair.A.Membership.LoadAsync();
            var b = await pair.B.Membership.LoadAsync();
            Assert.Equal(2, a.Members.Count());
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a.Log, SwarmRecordJson.Options), System.Text.Json.JsonSerializer.Serialize(b.Log, SwarmRecordJson.Options));
            Assert.All(a.Log, record => Assert.True(record.Verify(a.PublicKeyOf)));
            Assert.Equal(2, a.Log[^1].Signatures.Count);
            await pair.AssertBothDirectionsAsync();
        }
        else
        {
            if (outcome == "refused") pair.B.Join.Refuse(handshake.Offer.PairingId);
            else if (outcome == "expired") pair.Time.Advance(TimeSpan.FromMinutes(2));
            var failure = await Assert.ThrowsAsync<NodePairingException>(() => pair.A.Client.CompleteAsync(handshake));
            Assert.Equal(outcome, failure.Error);
            Assert.Empty((await pair.B.Membership.LoadAsync()).Log);
        }
    }
}
