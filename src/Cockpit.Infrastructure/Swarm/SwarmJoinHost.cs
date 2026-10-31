using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
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
    IEnumerable<ICockpitInternalMcpProvider> endpoints,
    IToastNotifier toast,
    NodeControllerPresence presence) : ISingletonService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;
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
            await RequestAsync(await _ReadAsync<SwarmJoinRequest>(context), false, context.RequestAborted))));
        group.MapPost("/migrate", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            _Desktop();
            var current = await settings.LoadAsync(context.RequestAborted);
            var token = context.Request.Headers.Authorization.ToString();
            if (token.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                token = token[7..];
            }
            var caller = await keys.AuthenticateAsync(token, current.SharedSecret, context.Connection.RemoteIpAddress?.ToString() ?? throw new InvalidOperationException("The request has no remote address."), context.RequestAborted);
            if (caller is null || (caller.ByConnectKey
                ? presence.Current?.KeyPrefix != caller.KeyPrefix
                : current.Pairing is null))
                throw NodePairingException.For(NodePairingError.InvalidToken, "Migration requires the paired controller's credential.");
            return await RequestAsync(await _ReadAsync<SwarmJoinRequest>(context), true, context.RequestAborted);
        })));
        group.MapPost("/join", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
            await PrepareAsync(await _ReadAsync<SwarmJoinPrepare>(context), context.RequestAborted))));
        group.MapPost("/join/commit", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            var transport = await CommitAsync(await _ReadAsync<SwarmJoinCommit>(context), context.RequestAborted);
            await services.GetRequiredService<SwarmJoinClient>().ConnectMembersAsync(context.RequestAborted);
            return transport;
        })));
        group.MapPost("/connect", (Delegate)((HttpContext context) => _HandleAsync(context, async () =>
        {
            _Desktop();
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
                if (current.Sequence == 0 || incoming.Sequence < current.Sequence || incoming.PublicKeyOf(peerId) is null
                    || request.Transport.Pin != NodeCertificatePin.FingerprintOf(peer)
                    || !_ExtendsLog(current, incoming))
                    throw NodePairingException.For(NodePairingError.InvalidToken, "Not a member of this swarm.");
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

    private void _Desktop()
    {
        if (host.Kind == SwarmHostKind.Server)
        {
            throw NodePairingException.For(SwarmJoinError.IsAServer, "A server keeps its existing connection.");
        }
    }

    internal async Task<NodePairingOffer> RequestAsync(SwarmJoinRequest request, bool migration, CancellationToken ct)
    {
        _Desktop();
        using var peer = X509CertificateLoader.LoadCertificate(request.Certificate);
        await _gate.WaitAsync(ct);
        try
        {
            if (Pending is not null)
            {
                throw NodePairingException.For(NodePairingError.PairingInProgress, "Another join is waiting.");
            }
            if (!migration)
            {
                if ((await settings.LoadAsync(ct)).Pairing is not null)
                {
                    throw NodePairingException.For(NodePairingError.AlreadyPaired, "Migrate the existing controller connection first.");
                }
                if ((await servers.LoadAsync(ct)).Any(server => server.IsNodeConnection))
                {
                    throw NodePairingException.For(NodePairingError.IsAController, "Migrate the existing node connections first.");
                }
            }
            var current = await membership.LoadAsync(ct);
            if (current.Sequence != 0 && !current.Tombstones.Any(item => item.Member == identity.MemberId))
            {
                throw NodePairingException.For(NodePairingError.AlreadyPaired, "This machine is already a swarm member.");
            }
            var offer = new NodePairingOffer(Guid.NewGuid().ToString("N"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Name, Time.GetUtcNow() + NodePairingBroker.Lifetime);
            _state = new(request, offer) { Confirmed = migration, Migration = migration };
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

    internal async Task<SwarmRecord> PrepareAsync(SwarmJoinPrepare request, CancellationToken ct)
    {
        _Desktop();
        await _gate.WaitAsync(ct);
        try
        {
            var state = _Claim(request.Claim);
            var register = SwarmMembership.Replay(request.Log);
            var previous = await membership.LoadAsync(ct);
            if (!_ExtendsLog(previous, register) || register.Sequence < previous.Sequence)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "Join must retain the signed removal history.");
            }
            using var peer = X509CertificateLoader.LoadCertificate(state.Request.Certificate);
            var peerId = SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo());
            if (register.PublicKeyOf(peerId) is null)
            {
                throw NodePairingException.For(NodePairingError.InvalidToken, "The adding machine is not a member.");
            }
            var local = await settings.LoadAsync(ct);
            var rank = SwarmRank.Draw(register.Log.Select(record => record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options))
                .OfType<SwarmMember>().Select(member => member.Rank));
            var member = new SwarmMember(certificate.Value.PublicKey.ExportSubjectPublicKeyInfo(), Name,
                rank, [NodeReachableAddress.Resolve() ?? "127.0.0.1"], local.Port, peerId, Time.GetUtcNow(), register.Sequence + 1);
            state.Register = register;
            state.Proposal = identity.Sign(SwarmMembership.ToPayload(member));
            return state.Proposal;
        }
        finally { _gate.Release(); }
    }

    internal async Task<SwarmTransport> CommitAsync(SwarmJoinCommit request, CancellationToken ct)
    {
        _Desktop();
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
            if (state.Migration)
            {
                await NotifyAsync(state.Request.Name, ct);
            }
            return transport;
        }
        finally { _gate.Release(); }
    }

    internal async Task<SwarmTransport> IssueTransportAsync(SwarmMember member, CancellationToken ct)
    {
        var issued = await keys.IssueAsync(SwarmKick.KeyLabel(member), ConnectKeyCapability.Admin, null,
            new NodeCaller(null, "swarm join", ConnectKeyCapability.Admin, "local", CancellationToken.None),
            scope: ConnectKeyScope.Everything, neverExpires: true, cancellationToken: ct);
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

    internal Task NotifyAsync(string name, CancellationToken ct) => toast.NotifyAsync(new AttentionNotification("Swarm", $"{name} is now a member of your swarm"), ct);

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
        public bool Migration { get; set; }
        public SwarmMembership? Register { get; set; }
        public SwarmRecord? Proposal { get; set; }
    }
}
