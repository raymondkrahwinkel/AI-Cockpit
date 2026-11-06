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
        await third.Leadership.RefreshAsync();
        Assert.True(third.Leadership.IsActive);
        await third.Kick.RemoveAsync(third.Identity.MemberId, default);
        await third.Heartbeat.TickAsync();
        Assert.False(third.Leadership.IsActive);
        Assert.Null(third.Leadership.Current);
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
        var current = await pair.A.Membership.LoadAsync();
        var oldMember = before.Members.Single(member => member.Id == third.Identity.MemberId);
        var replay = pair.A.Identity.Sign(SwarmMembership.ToPayload(oldMember with { Sequence = current.Sequence + 1 }));
        using var key = third.Certificate.Value.GetECDsaPrivateKey() ?? throw new InvalidOperationException("Expected a signing key.");
        Assert.Equal(SwarmMembershipVerdict.Removed, current.Accept(replay.CoSign(key)));
        var handshake = await third.Client.BeginAsync(pair.A.Address);
        Assert.Equal(pair.A.Join.Pending?.Code, handshake.Code);
        var pending = await Assert.ThrowsAsync<Cockpit.Core.Mcp.NodePairingException>(() => third.Client.CompleteAsync(handshake));
        Assert.Equal(Cockpit.Core.Mcp.NodePairingError.Pending, pending.Error);
        pair.A.Join.Confirm(handshake.Offer.PairingId);
        await third.Client.CompleteAsync(handshake);
        var rejoined = await pair.A.Membership.LoadAsync();
        Assert.NotEqual(oldRank, rejoined.Members.Single(member => member.Id == third.Identity.MemberId).Rank);
        Assert.NotNull((await third.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
        await pair.A.Heartbeat.TickAsync();
        Assert.NotNull((await pair.B.Membership.LoadAsync()).PublicKeyOf(third.Identity.MemberId));
        foreach (var server in credentials)
        {
            var machine = server.PinnedCertificateFingerprint == pair.A.Certificate.Fingerprint ? pair.A : pair.B;
            Assert.Null(await machine.Keys.AuthenticateAsync((server.ApiKey ?? throw new InvalidOperationException("Expected a value.")), "127.0.0.1"));
        }

        var currentRank = rejoined.Members.Single(member => member.Id == third.Identity.MemberId).Rank;
        var backup = await third.Heartbeat.ResetAsync(default);
        Assert.True(File.Exists(backup));
        Assert.Equal(rejoined.Sequence, SwarmMembership.Replay(System.Text.Json.JsonSerializer.Deserialize<List<SwarmRecord>>(
            await File.ReadAllTextAsync(backup), SwarmRecordJson.Options) ?? throw new InvalidDataException("Expected a saved log.")).Sequence);
        Assert.Empty((await third.Membership.LoadAsync()).Log);
        Assert.False(third.Leadership.IsActive);
        Assert.Null(third.Leadership.Current);
        await third.Heartbeat.ResetAsync(default);
        Assert.Empty((await third.Membership.LoadAsync()).Log);
        Assert.False(third.Leadership.IsActive);

        // A reset machine must be able to return even while its former peers still remember it.
        var resetHandshake = await third.Client.BeginAsync(pair.A.Address);
        Assert.Equal(pair.A.Join.Pending?.Code, resetHandshake.Code);
        var resetPending = await Assert.ThrowsAsync<Cockpit.Core.Mcp.NodePairingException>(() => third.Client.CompleteAsync(resetHandshake));
        Assert.Equal(Cockpit.Core.Mcp.NodePairingError.Pending, resetPending.Error);
        pair.A.Join.Confirm(resetHandshake.Offer.PairingId);
        await third.Client.CompleteAsync(resetHandshake);
        await pair.A.Heartbeat.TickAsync();
        await third.Heartbeat.TickAsync();
        var resetRejoined = await third.Membership.LoadAsync();
        Assert.NotEqual(currentRank, resetRejoined.Members.Single(member => member.Id == third.Identity.MemberId).Rank);
        Assert.Equal(SwarmPropagation.Prefix((await pair.A.Membership.LoadAsync()).Log), SwarmPropagation.Prefix(resetRejoined.Log));

        var enabled = await third.Settings.LoadAsync();
        await third.Settings.SaveAsync(enabled with { Enabled = false });
        await third.Heartbeat.TickAsync();
        Assert.False(third.Leadership.IsActive);
        Assert.Null(third.Leadership.Current);
        Assert.Equal(resetRejoined.Sequence, (await third.Membership.LoadAsync()).Sequence);
        await third.Settings.SaveAsync(enabled);
        await third.Heartbeat.TickAsync();
        Assert.True(third.Leadership.IsActive);

        // A fork may not silently overwrite a signed history; the operator explicitly abandons one side.
        var fork = await third.Membership.LoadAsync();
        Assert.Equal(SwarmMembershipVerdict.Accepted, fork.Accept(third.Identity.Sign(
            SwarmMembership.ToPayload(SwarmLeaderElection.ClaimLead(fork, third.Identity.MemberId)))));
        third.Membership.Save(fork);
        var conflictHandshake = await third.Client.BeginAsync(pair.A.Address);
        pair.A.Join.Confirm(conflictHandshake.Offer.PairingId);
        var conflict = await Assert.ThrowsAsync<Cockpit.Core.Mcp.NodePairingException>(() => third.Client.CompleteAsync(conflictHandshake));
        Assert.Contains("reset", conflict.Message, StringComparison.OrdinalIgnoreCase);
        await third.Heartbeat.ResetAsync(default);
        // The failed confirmed offer must be refused before a new two-screen confirmation starts.
        pair.A.Join.Refuse(conflictHandshake.Offer.PairingId);
        await JoinAsync(third, pair.A);
        await pair.A.Heartbeat.TickAsync();
        await third.Heartbeat.TickAsync();
        Assert.Equal(SwarmPropagation.Prefix((await pair.A.Membership.LoadAsync()).Log),
            SwarmPropagation.Prefix((await third.Membership.LoadAsync()).Log));
    }

    private static async Task JoinAsync(SwarmJoinFixture.Machine source, SwarmJoinFixture.Machine target)
    {
        var handshake = await source.Client.BeginAsync(target.Address);
        target.Join.Confirm(handshake.Offer.PairingId);
        await source.Client.CompleteAsync(handshake);
    }
}
