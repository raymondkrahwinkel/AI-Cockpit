using System.Net;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Swarm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Tests.Swarm;

public sealed class SwarmSessionChannelAuthTests
{
    [Fact]
    public async Task MissingCredentialsAndAKickedMemberReceiveNoTranscriptBytes()
    {
        await using var pair = await SwarmJoinFixture.CreateAsync();
        var handshake = await pair.A.Client.BeginAsync(pair.B.Address);
        pair.B.Join.Confirm(handshake.Offer.PairingId);
        await pair.A.Client.CompleteAsync(handshake);
        var kicked = (await pair.A.Membership.LoadAsync()).Members.Single(member => member.Id == pair.B.Identity.MemberId);
        await pair.A.Kick.RemoveAsync(kicked.Id, default);
        var kickedCaller = new NodeCaller("old-key", SwarmKick.KeyLabel(kicked), ConnectKeyCapability.Admin,
            "127.0.0.1", CancellationToken.None, Scope: ConnectKeyScope.Everything);

        var audit = new NodeAccessAuditLog(Path.Combine(AppContext.BaseDirectory, $"swarm-channel-{Guid.NewGuid():N}.jsonl"),
            NullLogger<NodeAccessAuditLog>.Instance);
        var policy = new SwarmNetworkPolicy(pair.A.SwarmSettings, audit, NullLogger<SwarmNetworkPolicy>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(pair.A.Membership).AddSingleton(policy);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Kicked"))
            {
                McpRequestContext.Set(null, kickedCaller);
            }
            try { await next(); }
            finally { McpRequestContext.Set(null); }
        });
        SwarmSessionEventsEndpoint.Map(app, app.Services);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.Single() ?? throw new InvalidOperationException("The app bound no address.");
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
        using var anonymous = await client.GetAsync("/swarm/session-events");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/swarm/session-events");
        request.Headers.Add("X-Kicked", "1");
        using var removed = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
        Assert.Empty(await anonymous.Content.ReadAsByteArrayAsync());
        Assert.Empty(await removed.Content.ReadAsByteArrayAsync());
    }
}
