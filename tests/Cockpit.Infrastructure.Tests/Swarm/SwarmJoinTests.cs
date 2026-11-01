using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public class SwarmJoinTests
{
    public static TheoryData<string> Outcomes => new()
    {
        "confirmed", "refused", "expired", "pending", "mesh", "offline", "node-connection",
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task JoinRequiresBothOperatorsAndGrantsBothDirections(string outcome)
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        // AC-1985: an existing master/node connection never blocks a join and stays beside the swarm.
        McpServerConfig[] legacy =
        [
            new() { Name = NodeServerName.For("Desktop", NodeServerName.SessionsServerName), IsNodeConnection = true, ApiKey = "pairing-secret" },
            new() { Name = NodeServerName.For("Server", NodeServerName.SessionsServerName), IsNodeConnection = true, ApiKey = "ck_server", Url = "https://10.0.0.2:20383/mcp" },
        ];
        if (outcome == "node-connection")
        {
            await pair.B.Servers.SaveAsync(legacy);
            var settings = await pair.B.Settings.LoadAsync();
            await pair.B.Settings.SaveAsync(settings with { Pairing = new NodePairing { ControllerName = "controller", ControllerAddress = "127.0.0.1", PairedAtUtc = DateTimeOffset.UtcNow } });
        }
        await using var third = await SwarmJoinFixture.Machine.CreateAsync(pair.Time, SwarmHostKind.Desktop);
        if (outcome is "mesh" or "offline")
        {
            var earlier = await pair.A.Client.BeginAsync(third.Address);
            third.Join.Confirm(earlier.Offer.PairingId);
            await pair.A.Client.CompleteAsync(earlier);
            if (outcome == "offline")
            {
                await third.StopAsync();
            }
        }
        var handshake = await pair.A.Client.BeginAsync(pair.B.Address);
        Assert.Equal((pair.B.Join.Pending ?? throw new InvalidOperationException("Expected a value.")).Code, handshake.Code);
        Assert.Equal(6, handshake.Code.Length);
        Assert.Equal(pair.B.Identity.MemberId, SwarmMemberId.FromPublicKey(handshake.PublicKey));
        Assert.NotEqual(pair.B.Certificate.Fingerprint, pair.B.Identity.MemberId.Value);
        if (outcome is "confirmed" or "mesh" or "offline" or "node-connection")
        {
            pair.B.Join.Confirm(handshake.Offer.PairingId);
            await pair.A.Client.CompleteAsync(handshake);
            var a = await pair.A.Membership.LoadAsync();
            var b = await pair.B.Membership.LoadAsync();
            Assert.Equal(outcome is "mesh" or "offline" ? 3 : 2, a.Members.Count());
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a.Log, SwarmRecordJson.Options), System.Text.Json.JsonSerializer.Serialize(b.Log, SwarmRecordJson.Options));
            Assert.All(a.Log, record => Assert.True(record.Verify(a.PublicKeyOf)));
            Assert.Equal(2, a.Log[^1].Signatures.Count);
            await pair.AssertBothDirectionsAsync();
            if (outcome == "mesh")
            {
                Assert.Equal(2, (await pair.B.Servers.LoadAsync()).Count);
                Assert.Equal(2, (await third.Servers.LoadAsync()).Count);
            }
            if (outcome == "node-connection")
            {
                var kept = await pair.B.Servers.LoadAsync();
                Assert.All(legacy, row => Assert.Contains(kept, server => server.Name == row.Name));
            }
        }
        else
        {
            if (outcome == "refused")
            {
                pair.B.Join.Refuse(handshake.Offer.PairingId);
            }
            else if (outcome == "expired")
            {
                pair.Time.Advance(TimeSpan.FromMinutes(2));
            }
            var failure = await Assert.ThrowsAsync<NodePairingException>(() => pair.A.Client.CompleteAsync(handshake));
            Assert.Equal(outcome, failure.Error);
            Assert.Empty((await pair.B.Membership.LoadAsync()).Log);
        }
    }
}
