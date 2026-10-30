using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmJoinClient(
    SwarmHost host,
    NodeSelfSignedCertificate certificate,
    SwarmIdentity identity,
    SwarmMembershipStore membership,
    SwarmNetworkPolicy network,
    SwarmJoinHost local,
    SwarmManualAddress manual,
    INodeEndpointSettingsStore settings,
    NodeDiscoveryClient discovery,
    ILogger<SwarmJoinClient>? logger = null,
    IServiceProvider? services = null) : ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<IReadOnlyList<NodeDiscoveryFound>> FindAsync(TimeSpan timeout, CancellationToken ct = default) =>
        discovery.FindAsync(timeout, network.IsAllowedAsync, ct);

    public Task<SwarmJoinHandshake> BeginAsync(string address, CancellationToken ct = default) => _BeginAsync(address, null, null, ct);

    internal async Task<SwarmJoinHandshake> _BeginAsync(string address, string? credential, string? expectedPin, CancellationToken ct)
    {
        if (host.Kind == SwarmHostKind.Server) throw NodePairingException.For(SwarmJoinError.IsAServer, "A server keeps its existing connections.");
        if (!(await settings.LoadAsync(ct)).Enabled) throw NodePairingException.For(NodePairingError.NotVisible, "Enable the node endpoint first.");
        var uri = SwarmManualAddress.Parse(address);
        if (uri.Scheme != "https") throw NodePairingException.For(NodePairingError.InvalidToken, "Join requires TLS.");
        string? pin = null;
        byte[]? publicKey = null;
        using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
        {
            if (remote is null) return false;
            pin = NodeCertificatePin.FingerprintOf(remote);
            if (expectedPin is not null && !ConnectKeyVerifier.ConstantTimeEquals(pin, expectedPin)) return false;
            using var peer = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
            publicKey = peer.PublicKey.ExportSubjectPublicKeyInfo();
            return true;
        };
        using var client = new HttpClient(handler) { BaseAddress = uri, Timeout = NodeConnectionFailure.DefaultBudget };
        if (credential is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        var request = new SwarmJoinRequest(local.Name, certificate.Value.RawData, "");
        var offer = await _PostAsync<SwarmJoinRequest, NodePairingOffer>(client, credential is null ? "swarm/request" : "swarm/migrate", request, ct);
        if (pin is null || publicKey is null) throw NodePairingException.For(NodePairingError.InvalidToken, "No TLS identity.");
        return new(uri.ToString(), offer, NodePairingCode.Derive(offer.Nonce, pin), pin, publicKey);
    }

    public async Task CompleteAsync(SwarmJoinHandshake handshake, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var register = await membership.LoadAsync(ct);
            if (register.Sequence == 0)
            {
                var options = await settings.LoadAsync(ct);
                var founder = new SwarmMember(certificate.Value.PublicKey.ExportSubjectPublicKeyInfo(), local.Name,
                    SwarmRank.Draw([]), [NodeReachableAddress.Resolve() ?? "127.0.0.1"], options.Port, identity.MemberId, DateTimeOffset.UtcNow, 1);
                if (register.Accept(identity.Sign(SwarmMembership.ToPayload(founder))) != SwarmMembershipVerdict.Accepted)
                    throw new InvalidDataException("Cannot create the local swarm.");
            }
            using var client = new HttpClient(NodeCertificatePin.Require(handshake.Pin, network.ConnectAsync)) { BaseAddress = new(handshake.Address) };
            var claim = new NodePairingClaimRequest(handshake.Offer.PairingId, handshake.Offer.ClaimToken);
            var proposal = await _PostAsync<SwarmJoinPrepare, SwarmRecord>(client, "swarm/join", new(claim, register.Log), ct);
            var member = proposal.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options) as SwarmMember;
            var peerId = SwarmMemberId.FromPublicKey(handshake.PublicKey);
            if (member is null || member.Id != peerId || member.AddedBy != identity.MemberId
                || register.Members.Any(existing => existing.Rank == member.Rank)
                || !proposal.Verify(id => id == peerId ? handshake.PublicKey : null))
                throw NodePairingException.For(NodePairingError.InvalidToken, "Invalid join proposal.");
            using var key = certificate.Value.GetECDsaPrivateKey() ?? throw new InvalidOperationException("No signing key.");
            var signed = proposal.CoSign(key);
            if (register.Accept(signed) != SwarmMembershipVerdict.Accepted) throw new InvalidDataException("Invalid membership change.");
            var reverse = await local.IssueTransportAsync(handshake.Offer.NodeName, ct);
            var transport = await _PostAsync<SwarmJoinCommit, SwarmTransport>(client, "swarm/join/commit", new(claim, signed, reverse), ct);
            if (transport.Pin != handshake.Pin) throw NodePairingException.For(NodePairingError.InvalidToken, "Unexpected transport pin.");
            await local.StoreTransportAsync(handshake.Offer.NodeName, transport, ct);
            if (services is null) membership.Save(register);
            else await services.GetRequiredService<SwarmPropagation>().ApplyAsync(new(0, null, register.Log), ct);
        }
        finally { _gate.Release(); }
        if (services is not null) await services.GetRequiredService<SwarmHeartbeat>().TickAsync(ct: ct);
    }

    internal async Task ConnectMembersAsync(CancellationToken ct)
    {
        var register = await membership.LoadAsync(ct);
        var self = register.Members.Single(member => member.Id == identity.MemberId);
        foreach (var member in register.Members.Where(member => member.Id != identity.MemberId && member.Id != self.AddedBy))
        {
            try { await ConnectMemberAsync(register, member, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or NodePairingException)
            {
                logger?.LogWarning(ex, "Swarm member {Member} could not be connected after joining.", member.Id);
            }
        }
    }

    internal async Task ConnectMemberAsync(SwarmMembership register, SwarmMember member, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate.Value };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
        {
            if (remote is null) return false;
            using var peer = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
            return SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo()) == member.Id;
        };
        var manualUri = await manual.ForMemberAsync(member.Id, ct);
        var address = manualUri?.IdnHost ?? member.Addresses.FirstOrDefault() ?? throw new InvalidDataException("Member has no address.");
        using var client = new HttpClient(handler) { BaseAddress = manualUri ?? new UriBuilder("https", address, member.Port).Uri, Timeout = TimeSpan.FromSeconds(5) };
        var reverse = await local.IssueTransportAsync(member.Name, ct);
        var transport = await _PostAsync<SwarmMeshRequest, SwarmTransport>(client, "swarm/connect", new(register.Log, reverse), ct);
        await local.StoreTransportAsync(member.Name, transport, ct);
    }

    private static async Task<TOut> _PostAsync<TIn, TOut>(HttpClient client, string path, TIn value, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, value, SwarmRecordJson.Options, ct);
        if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Accepted)
        {
            var problem = await response.Content.ReadFromJsonAsync<NodePairingProblem>(NodePairingJson.Options, ct);
            throw new NodePairingException(problem ?? new(NodePairingError.InvalidToken, "Empty join response."));
        }
        return await response.Content.ReadFromJsonAsync<TOut>(SwarmRecordJson.Options, ct) ?? throw new InvalidDataException("Empty join response.");
    }
}
