using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;

namespace Cockpit.Infrastructure.Tests.Swarm;

public class SwarmKickTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RemovalRevokesEveryPeerRequiresFreshPairingAndRejectsUntrustedRequests(int criterion)
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        await using var third = await SwarmJoinFixture.Machine.CreateAsync(pair.Time, SwarmHostKind.Desktop);
        await JoinAsync(pair.A, pair.B);
        await JoinAsync(pair.A, third);
        await pair.A.Heartbeat.TickAsync();
        await pair.B.Heartbeat.TickAsync();
        var before = await pair.A.Membership.LoadAsync();
        var oldRank = before.Members.Single(member => member.Id == third.Identity.MemberId).Rank;
        var credentials = (await third.Servers.LoadAsync()).ToArray();
        var heartbeat = await third.Heartbeat.OwnAsync(false, default);
        if (criterion == 3)
        {
            var signed = pair.A.Identity.Sign(SwarmMembership.ToPayload(new SwarmTombstone(third.Identity.MemberId,
                pair.A.Identity.MemberId, DateTimeOffset.UtcNow, before.Sequence + 1)));
            using var handler = new SocketsHttpHandler();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, peer, _, _) => peer is not null
                && NodeCertificatePin.FingerprintOf(peer) == pair.B.Certificate.Fingerprint;
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { pair.A.Certificate.Value };
            using var client = new HttpClient(handler) { BaseAddress = new(pair.B.Address) };
            foreach (var unsigned in new[] { true, false })
            {
                pair.B.External = !unsigned;
                using var response = await client.PostAsJsonAsync("swarm/removal", new SwarmRecords(0, null,
                    [.. before.Log, unsigned ? signed with { Signatures = [] } : signed]), SwarmRecordJson.Options);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal(before.Sequence, (await pair.B.Membership.LoadAsync()).Sequence);
            }
            pair.B.External = false;
            return;
        }
        pair.B.Sleep = true;
        await pair.A.Kick.RemoveAsync(third.Identity.MemberId, default);
        Assert.Contains("removed_from_swarm", await third.Kick.StatusAsync(default));
        Assert.Contains("removal_pending", await pair.A.Kick.StatusAsync(default));
        Assert.NotNull((await pair.B.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
        pair.B.Sleep = false;
        await pair.A.Heartbeat.TickAsync();
        foreach (var machine in new[] { pair.A, pair.B })
        {
            Assert.Null((await machine.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
            Assert.Null(await machine.Heartbeat.AcceptAsync(heartbeat, third.Identity.MemberId, third.Certificate.Fingerprint, default));
            var credential = credentials.Single(server => server.PinnedCertificateFingerprint == machine.Certificate.Fingerprint);
            using var client = new HttpClient(NodeCertificatePin.Require(machine.Certificate.Fingerprint));
            client.DefaultRequestHeaders.Authorization = new("Bearer", credential.ApiKey);
            using var response = await client.GetAsync(credential.Url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        if (criterion == 1) return;
        var current = await pair.A.Membership.LoadAsync();
        var oldMember = before.Members.Single(member => member.Id == third.Identity.MemberId);
        var replay = pair.A.Identity.Sign(SwarmMembership.ToPayload(oldMember with { Sequence = current.Sequence + 1 }));
        using var key = third.Certificate.Value.GetECDsaPrivateKey()!;
        Assert.Equal(SwarmMembershipVerdict.Removed, current.Accept(replay.CoSign(key)));
        var handshake = await pair.A.Client.BeginAsync(third.Address);
        var pending = await Assert.ThrowsAsync<Cockpit.Core.Mcp.NodePairingException>(() => pair.A.Client.CompleteAsync(handshake));
        Assert.Equal(Cockpit.Core.Mcp.NodePairingError.Pending, pending.Error);
        third.Join.Confirm(handshake.Offer.PairingId);
        await pair.A.Client.CompleteAsync(handshake);
        var rejoined = await pair.A.Membership.LoadAsync();
        Assert.NotEqual(oldRank, rejoined.Members.Single(member => member.Id == third.Identity.MemberId).Rank);
        Assert.NotNull((await third.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
        await pair.A.Heartbeat.TickAsync();
        Assert.NotNull((await pair.B.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
        foreach (var server in credentials)
        {
            var machine = server.PinnedCertificateFingerprint == pair.A.Certificate.Fingerprint ? pair.A : pair.B;
            Assert.Null(await machine.Keys.AuthenticateAsync(server.ApiKey!, null, "127.0.0.1"));
        }
    }

    private static async Task JoinAsync(SwarmJoinFixture.Machine source, SwarmJoinFixture.Machine target)
    {
        var handshake = await source.Client.BeginAsync(target.Address);
        target.Join.Confirm(handshake.Offer.PairingId);
        await source.Client.CompleteAsync(handshake);
    }
}
