using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmJoinHost(
    SwarmHost host,
    NodeSelfSignedCertificate certificate,
    SwarmIdentity identity,
    SwarmMembershipStore membership,
    ConnectKeyVerifier keys,
    INodeEndpointSettingsStore settings,
    IMcpServerStore servers,
    IEnumerable<ICockpitInternalMcpProvider> endpoints) : ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;
    internal async Task ResetAsync(Func<Task> reset, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _state = null;
            await reset();
        }
        finally
        {
            _gate.Release();
        }
    }
    internal TimeProvider Time { get; set; } = TimeProvider.System;
    internal string Name { get; set; } = Environment.MachineName;
    public SwarmJoinPending? Pending => _state is { Used: false, Refused: false } state && state.Offer.ExpiresAtUtc > Time.GetUtcNow()
        ? new(state.Offer.PairingId, state.Request.Name, NodePairingCode.Derive(state.Offer.Nonce, certificate.Fingerprint), state.Offer.ExpiresAtUtc) : null;

    public void Confirm(string id)
    {
        _gate.Wait();
        try
        {
            var state = _Get(id);
            state.Confirmed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Refuse(string id)
    {
        _gate.Wait();
        try
        {
            _Get(id).Refused = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void MapRoutes(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        var group = SwarmNetworkPolicy.MapGroup(routes, services);
        group.MapPost("/request", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
            await RequestAsync(await _ReadAsync<SwarmJoinRequest>(context), context.RequestAborted))));
        group.MapPost("/join", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            var request = await _ReadAsync<SwarmJoinPrepare>(context);
            var prepared = await PrepareAsync(request, context.RequestAborted);
            return request.IncludeLog ? (object)prepared : prepared.Record;
        })));
        group.MapPost("/join/commit", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            var transport = await CommitAsync(await _ReadAsync<SwarmJoinCommit>(context), context.RequestAborted);
            await services.GetRequiredService<SwarmJoinClient>().ConnectMembersAsync(context.RequestAborted);
            return transport;
        })));
        group.MapPost("/connect", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            await _DesktopAsync(context.RequestAborted);
            using var peer = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (peer is null)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "A member must present its TLS certificate.");
            }
            var request = await _ReadAsync<SwarmMeshRequest>(context);
            await _gate.WaitAsync(context.RequestAborted);
            try
            {
                var current = await membership.LoadAsync(context.RequestAborted);
                var incoming = SwarmMembership.Replay(request.Log);
                var peerId = SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo());
                if (current.PublicKeyOf(identity.MemberId) is null || incoming.Sequence < current.Sequence || incoming.PublicKeyOf(peerId) is null
                    || request.Transport.Pin != NodeCertificatePin.FingerprintOf(peer)
                    || !_ExtendsLog(current, incoming))
                {
                    throw NodePairingException.For(NodePairingError.InvalidToken, "Not a member of this swarm.");
                }
                var name = incoming.Members.Single(member => member.Id == peerId).Name;
                await StoreTransportAsync(name, request.Transport, context.RequestAborted);
                var transport = await IssueTransportAsync(incoming.Members.Single(member => member.Id == peerId), context.RequestAborted);
                await services.GetRequiredService<SwarmPropagation>().ApplyAsync(new(0, null, incoming.Log), context.RequestAborted);
                return transport;
            }
            finally { _gate.Release(); }
        })));
    }

    private static bool _ExtendsLog(SwarmMembership current, SwarmMembership incoming) =>
        current.Log.Zip(incoming.Log).All(pair =>
            System.Text.Json.JsonSerializer.Serialize(pair.First, SwarmRecordJson.Options)
            == System.Text.Json.JsonSerializer.Serialize(pair.Second, SwarmRecordJson.Options));

    private async Task _DesktopAsync(CancellationToken ct)
    {
        if (host.Kind == SwarmHostKind.Server)
        {
            throw NodePairingException.For(SwarmJoinError.IsAServer, "A server keeps its existing connection.");
        }
        if (!(await settings.LoadAsync(ct)).Enabled)
        {
            throw NodePairingException.For(NodePairingError.NotVisible, "Accept connections is off on this machine. Enable it before pairing.");
        }
    }

    internal async Task<NodePairingOffer> RequestAsync(SwarmJoinRequest request, CancellationToken ct)
    {
        await _DesktopAsync(ct);
        using var peer = X509CertificateLoader.LoadCertificate(request.Certificate);
        await _gate.WaitAsync(ct);
        try
        {
            if (SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo()) == identity.MemberId)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "Choose another machine to pair with.");
            }
            if (Pending is not null && _state is { } waiting && !waiting.Request.Certificate.SequenceEqual(request.Certificate))
            {
                throw NodePairingException.For(NodePairingError.PairingInProgress, "Another join is waiting.");
            }
            var offer = new NodePairingOffer(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Name, Time.GetUtcNow() + NodePairingBroker.Lifetime);
            _state = new(request, offer);
            return offer;
        }
        finally { _gate.Release(); }
    }

    private State _Get(string id)
    {
        var state = _state;
        if (state is null || state.Offer.PairingId != id)
        {
            throw NodePairingException.For(NodePairingError.InvalidToken, "Unknown join.");
        }
        if (state.Used)
        {
            throw NodePairingException.For(NodePairingError.AlreadyUsed, "Join already used.");
        }
        if (state.Refused)
        {
            throw NodePairingException.For(NodePairingError.Refused, "Join refused on this machine.");
        }
        if (Time.GetUtcNow() >= state.Offer.ExpiresAtUtc)
        {
            throw NodePairingException.For(NodePairingError.Expired, "Join expired.");
        }
        return state;
    }

    private State _Claim(NodePairingClaimRequest claim)
    {
        var state = _Get(claim.PairingId);
        if (!ConnectKeyVerifier.ConstantTimeEquals(state.Offer.ClaimToken, claim.ClaimToken))
        {
            throw NodePairingException.For(NodePairingError.InvalidToken, "Invalid claim token.");
        }
        if (!state.Confirmed)
        {
            throw NodePairingException.For(NodePairingError.Pending, "Confirm the code on this machine.");
        }
        return state;
    }

    internal async Task<SwarmJoinProposal> PrepareAsync(SwarmJoinPrepare request, CancellationToken ct)
    {
        await _DesktopAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var state = _Claim(request.Claim);
            var register = SwarmMembership.Replay(request.Log);
            var previous = await membership.LoadAsync(ct);
            using var peer = X509CertificateLoader.LoadCertificate(state.Request.Certificate);
            var peerKey = peer.PublicKey.ExportSubjectPublicKeyInfo();
            var peerId = SwarmMemberId.FromPublicKey(peerKey);
            // A former member may initiate its own return. The remaining member supplies the authoritative log.
            var returning = previous.PublicKeyOf(identity.MemberId) is not null;
            if (returning)
            {
                if (register.Sequence > 1 && (register.Sequence > previous.Sequence || !_ExtendsLog(register, previous)))
                {
                    throw NodePairingException.For(NodePairingError.InvalidToken, "Swarm histories differ. Use Leave / reset swarm on the machine abandoning its swarm, then pair from that machine again.");
                }
                register = previous;
                if (register.PublicKeyOf(peerId) is not null)
                {
                    var removal = identity.Sign(SwarmMembership.ToPayload(new SwarmTombstone(peerId,
                        identity.MemberId, Time.GetUtcNow(), register.Sequence + 1)));
                    if (register.Accept(removal) != SwarmMembershipVerdict.Accepted)
                    {
                        throw NodePairingException.For(NodePairingError.InvalidToken, "Cannot renew this membership. Reset the joining machine first.");
                    }
                }
            }
            else if (!_ExtendsLog(previous, register) || register.Sequence < previous.Sequence)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "This machine has a different swarm history. Use Leave / reset swarm here before pairing again.");
            }
            if (!returning && register.PublicKeyOf(peerId) is null)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "The adding machine is not a member.");
            }
            var local = await settings.LoadAsync(ct);
            var ranks = register.Log.Select(record => record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options))
                .SelectMany(change => change switch
                {
                    SwarmMember added => new[] { added.Rank },
                    SwarmRankChange changed => new[] { changed.Rank },
                    _ => Array.Empty<long>(),
                });
            var member = new SwarmMember(returning ? peerKey : certificate.Value.PublicKey.ExportSubjectPublicKeyInfo(),
                returning ? state.Request.Name : Name, SwarmRank.Draw(ranks),
                returning ? state.Request.Addresses : [NodeReachableAddress.Resolve() ?? "127.0.0.1"],
                returning ? state.Request.Port : local.Port, returning ? identity.MemberId : peerId,
                Time.GetUtcNow(), register.Sequence + 1);
            state.Register = register;
            state.Proposal = identity.Sign(SwarmMembership.ToPayload(member));
            return new(state.Proposal, register.Log);
        }
        finally { _gate.Release(); }
    }

    internal async Task<SwarmTransport> CommitAsync(SwarmJoinCommit request, CancellationToken ct)
    {
        await _DesktopAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var state = _Claim(request.Claim);
            using var peer = X509CertificateLoader.LoadCertificate(state.Request.Certificate);
            if (state.Proposal is null || state.Register is null
                || !SwarmRecordJson.Canonicalize(request.Record.Payload).SequenceEqual(SwarmRecordJson.Canonicalize(state.Proposal.Payload))
                || request.Transport.Pin != NodeCertificatePin.FingerprintOf(peer)
                || state.Register.Accept(request.Record) != SwarmMembershipVerdict.Accepted)
                throw NodePairingException.For(NodePairingError.InvalidToken, "Invalid signed join.");
            await StoreTransportAsync(state.Request.Name, request.Transport, ct);
            var transport = await IssueTransportAsync(state.Register.Members.Single(member => member.Id == SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo())), ct);
            membership.Save(state.Register);
            state.Used = true;
            return transport;
        }
        finally { _gate.Release(); }
    }

    internal async Task<SwarmTransport> IssueTransportAsync(SwarmMember member, CancellationToken ct)
    {
        var issued = await keys.IssueAsync(SwarmKick.KeyLabel(member), ConnectKeyCapability.Admin, null,
            new NodeCaller(null, "swarm join", ConnectKeyCapability.Admin, "local", CancellationToken.None),
            scope: ConnectKeyScope.Everything, neverExpires: true, issuedToOwner: true, cancellationToken: ct);
        return new(issued.Secret, certificate.Fingerprint, [.. endpoints.SelectMany(endpoint => endpoint.GetNodeAddresses())]);
    }

    internal async Task StoreTransportAsync(string name, SwarmTransport transport, CancellationToken ct)
    {
        var known = await servers.LoadAsync(ct);
        var added = transport.Endpoints.Select(endpoint => new McpServerConfig
        {
            Name = NodeServerName.For(name, endpoint.ServerName), Id = Guid.NewGuid().ToString("N"),
            IsNodeConnection = true, Transport = McpTransport.Http, Url = endpoint.Url,
            Auth = McpServerAuth.ApiKey, ApiKey = transport.ConnectKey, PinnedCertificateFingerprint = transport.Pin,
        });
        await servers.SaveAsync([.. known.Where(server => !server.Name.StartsWith(NodeServerName.PrefixFor(name), StringComparison.Ordinal)), .. added], ct);
    }

    private static async Task<T> _ReadAsync<T>(HttpContext context) where T : class =>
        await context.Request.ReadFromJsonAsync<T>(SwarmRecordJson.Options, context.RequestAborted)
        ?? throw NodePairingException.For(NodePairingError.InvalidToken, "Expected a JSON request.");

    private static async Task<IResult> _HandleAsync(HttpContext context, Func<Task<object>> action)
    {
        try { return Results.Json(await action(), SwarmRecordJson.Options); }
        catch (NodePairingException ex)
        {
            var status = ex.Error == NodePairingError.Pending ? 202 : ex.Error is NodePairingError.Expired or NodePairingError.AlreadyUsed or NodePairingError.Refused ? 410 : 403;
            return Results.Json(ex.Problem, NodePairingJson.Options, statusCode: status);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or CryptographicException or InvalidDataException or BadHttpRequestException)
        {
            return Results.Json(new NodePairingProblem(NodePairingError.InvalidToken, "Invalid join request."), NodePairingJson.Options, statusCode: 400);
        }
    }

    private sealed class State(SwarmJoinRequest request, NodePairingOffer offer)
    {
        public SwarmJoinRequest Request { get; } = request;
        public NodePairingOffer Offer { get; } = offer;
        public bool Confirmed { get; set; }
        public bool Refused { get; set; }
        public bool Used { get; set; }
        public SwarmMembership? Register { get; set; }
        public SwarmRecord? Proposal { get; set; }
    }
}
