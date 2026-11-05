using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Swarm;

internal sealed class SwarmClaimClient(SwarmMembershipStore store, SwarmIdentity identity,
    NodeSelfSignedCertificate certificate, IMcpServerStore servers, SwarmNetworkPolicy network,
    SwarmManualAddress manual, SwarmPropagation propagation) : ISingletonService
{
    internal async Task ClaimAsync(SwarmMember leader, CancellationToken ct)
    {
        var register = await store.LoadAsync(ct);
        var record = identity.Sign(SwarmMembership.ToPayload(SwarmLeaderElection.ClaimLead(register, identity.MemberId)));
        var server = (await servers.LoadAsync(ct)).FirstOrDefault(item =>
            item.Name == NodeServerName.For(leader.Name, NodeServerName.SessionsServerName))
            ?? throw new InvalidOperationException("The leader is unreachable. Wait for the normal election.");
        if (string.IsNullOrWhiteSpace(server.PinnedCertificateFingerprint) || string.IsNullOrWhiteSpace(server.ApiKey))
        {
            throw new InvalidOperationException("The leader has no trusted connection.");
        }
        if (Uri.TryCreate(server.Url, UriKind.Absolute, out var configured) == false || configured.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("The leader has no trusted connection.");
        }
        var endpoint = await manual.ForMemberAsync(leader.Id, ct) ?? configured;
        using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate.Value };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, _) =>
            remote is not null && NodeCertificatePin.FingerprintOf(remote) == server.PinnedCertificateFingerprint;
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint.GetLeftPart(UriPartial.Authority)),
            Timeout = NodeConnectionFailure.DefaultBudget,
        };
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.ApiKey);
        using var response = await client.PostAsJsonAsync("swarm/claim", record, SwarmRecordJson.Options, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException("The leader or rank order changed. Wait for the swarm to update, then try again.");
        }
        response.EnsureSuccessStatusCode();
        var delta = await response.Content.ReadFromJsonAsync<SwarmRecords>(SwarmRecordJson.Options, ct)
            ?? throw new InvalidDataException("The leader returned no rank change.");
        await propagation.ApplyAsync(delta, ct);
    }
}
