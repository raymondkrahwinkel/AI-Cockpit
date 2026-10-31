using System.Net.Http.Json;
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
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmKick(SwarmMembershipStore store, SwarmIdentity identity,
    NodeSelfSignedCertificate certificate, ConnectKeyVerifier keys, IMcpServerStore servers,
    SwarmNetworkPolicy network, SwarmManualAddress manual, ILogger<SwarmKick> logger) : ISingletonService
{
    internal event Action<SwarmMember>? RemovalRequested;
    internal void RequestRemoval(SwarmMember member) => RemovalRequested?.Invoke(member);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SwarmMemberId, long> _delivered = new();
    internal static string KeyLabel(SwarmMember member) => $"swarm:{member.Id.Value}:{member.Rank}";

    internal async Task RemoveAsync(SwarmMemberId member, CancellationToken ct)
    {
        await store.Gate.WaitAsync(ct);
        try
        {
            var register = await store.LoadAsync(ct);
            var record = identity.Sign(SwarmMembership.ToPayload(new SwarmTombstone(member, identity.MemberId,
                DateTimeOffset.UtcNow, register.Sequence + 1)));
            if (register.Accept(record) != SwarmMembershipVerdict.Accepted)
            {
                throw new InvalidDataException("swarm_removal_refused");
            }
            store.Save(register);
            await RevokeAsync(register, ct);
        }
        finally { store.Gate.Release(); }
        await DeliverAsync(ct);
    }

    internal async Task RevokeAsync(SwarmMembership register, CancellationToken ct)
    {
        var removed = register.Tombstones.Select(item => item.Member).ToHashSet();
        var left = removed.Contains(identity.MemberId);
        foreach (var (key, _) in await keys.ListAsync(ct))
        {
            if (key.RevokedAt is null && key.Label.StartsWith("swarm:", StringComparison.Ordinal) &&
                (left || !register.Members.Any(member => key.Label == KeyLabel(member))))
            {
                await keys.RevokeAsync(key.Prefix, new NodeCaller(null, "swarm removal", ConnectKeyCapability.Admin, "local", CancellationToken.None), ct);
            }
        }
        var names = register.Log.Select(record => record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options))
            .OfType<SwarmMember>().Where(member => left || removed.Contains(member.Id)).Select(member => NodeServerName.PrefixFor(member.Name)).ToArray();
        var known = await servers.LoadAsync(ct);
        var kept = known.Where(server => !server.IsNodeConnection || !names.Any(name => server.Name.StartsWith(name, StringComparison.Ordinal))).ToArray();
        if (kept.Length != known.Count)
        {
            await servers.SaveAsync(kept, ct);
        }
    }

    internal async Task<string> StatusAsync(CancellationToken ct)
    {
        var register = await store.LoadAsync(ct);
        var tombstone = register.Tombstones.FirstOrDefault(item => item.Member == identity.MemberId);
        if (tombstone is not null)
        {
            var name = register.Log.Select(record => record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options))
                .OfType<SwarmMember>().First(member => member.Id == tombstone.RemovedBy).Name;
            return $"{name} removed this machine from the swarm. Your sessions here keep running. Join again with confirmation on both screens and a new rank. removed_from_swarm";
        }
        if (!register.Tombstones.Any())
        {
            return "";
        }
        return register.Members.Any(member => member.Id != identity.MemberId && _delivered.GetValueOrDefault(member.Id) < register.Sequence)
            ? "Kicked · removal_pending: sleeping members learn the signed removal at their next contact."
            : "Kicked. The signed removal reached every member.";
    }

    internal async Task DeliverAsync(CancellationToken ct)
    {
        var register = await store.LoadAsync(ct);
        await RevokeAsync(register, ct);
        if (!register.Tombstones.Any())
        {
            return;
        }
        var peers = register.Log.Select(record => record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options))
            .OfType<SwarmMember>().DistinctBy(member => member.Id).Where(member => member.Id != identity.MemberId);
        await Task.WhenAll(peers.Where(member => _delivered.GetValueOrDefault(member.Id) < register.Sequence).Select(async member =>
        {
            try
            {
                using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
                handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate.Value };
                handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
                {
                    if (remote is null)
                    {
                        return false;
                    }
                    using var peer = X509CertificateLoader.LoadCertificate(remote.GetRawCertData());
                    return SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo()) == member.Id;
                };
                var address = await manual.ForMemberAsync(member.Id, ct) ?? new UriBuilder("https", member.Addresses[0], member.Port).Uri;
                using var client = new HttpClient(handler) { BaseAddress = address,
                    Timeout = NodeConnectionFailure.DefaultBudget };
                using var response = await client.PostAsJsonAsync("swarm/removal", new SwarmRecords(0, null, register.Log), SwarmRecordJson.Options, ct);
                response.EnsureSuccessStatusCode();
                _delivered[member.Id] = register.Sequence;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                logger.LogWarning(ex, "Swarm removal pending for {Member}.", member.Id);
            }
        }));
    }

    public void MapRoutes(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        SwarmNetworkPolicy.MapGroup(routes, services).MapPost("/removal", (Delegate)(async (HttpContext context) =>
        {
            using var peer = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (peer is null)
            {
                return Results.Unauthorized();
            }
            var register = await store.LoadAsync(context.RequestAborted);
            var id = SwarmMemberId.FromPublicKey(peer.PublicKey.ExportSubjectPublicKeyInfo());
            if (register.PublicKeyOf(id) is null)
            {
                return Results.StatusCode(403);
            }
            try
            {
                var delta = await context.Request.ReadFromJsonAsync<SwarmRecords>(SwarmRecordJson.Options, context.RequestAborted);
                if (delta is null || delta.Records is null || delta.Records.Any(record => record is null || record.Signatures is null) ||
                    !delta.Records.Any(record =>
                    record.Payload.Deserialize<SwarmChange>(SwarmRecordJson.Options) is SwarmTombstone))
                {
                    return Results.BadRequest();
                }
                await services.GetRequiredService<SwarmPropagation>().ApplyAsync(delta, context.RequestAborted);
                return Results.Ok();
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            {
                return Results.StatusCode(403);
            }
        }));
    }
}
