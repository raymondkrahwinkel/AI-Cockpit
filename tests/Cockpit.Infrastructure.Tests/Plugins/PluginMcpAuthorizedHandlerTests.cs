using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Plugins;

// AC-1587: the handler a plugin gets for its own MCP server's other routes. The token must reach the server and
// nothing the plugin holds: not its request, not the response, not an exception, and never another origin.
public sealed class PluginMcpAuthorizedHandlerTests
{
    private const string Token = "secret-bearer-4b1d";
    private const string RenewedToken = "renewed-bearer-9c2e";

    private static readonly McpServerConfig Depot = new()
    {
        Name = "Depot: Work",
        Url = "https://depot.example.com/mcp",
        Auth = McpServerAuth.OAuth,
        OAuthAuthority = "https://depot.example.com",
    };

    [Theory]
    [InlineData("https://depot.example.com/cockpit/inbox")]
    [InlineData("wss://depot.example.com/hubs/inbox?expectedUser=u1")]
    public async Task Send_ToItsOwnOrigin_PutsTheBearerOnTheWireAndNowhereThePluginCanSee(string target)
    {
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(_Handler(_Coordinator(), upstream));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("X-Plugin", "kept");

        using var response = await client.SendAsync(request);

        var sent = Assert.Single(upstream.Requests);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("kept", sent.PluginHeader);
        Assert.DoesNotContain(Token, _EverythingThePluginSees(request, response), StringComparison.Ordinal);
        Assert.Same(request, response.RequestMessage);
    }

    [Theory]
    [InlineData("https://evil.example.com/cockpit/inbox")]
    [InlineData("http://depot.example.com/cockpit/inbox")]
    [InlineData("https://depot.example.com:8443/cockpit/inbox")]
    [InlineData("wss://evil.example.com/hubs/inbox")]
    [InlineData("https://depot.example.com.evil.example.com/cockpit/inbox")]
    public async Task Send_ToAnyOtherOrigin_IsRefusedBeforeACredentialIsAskedFor(string target)
    {
        var coordinator = _Coordinator();
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(_Handler(coordinator, upstream));

        var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(target));

        Assert.Empty(upstream.Requests);
        await coordinator.DidNotReceiveWithAnyArgs().AcquireAsync(default!, default, default);
        Assert.DoesNotContain(Token, refusal.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WithAHostHeaderNamingAnotherSite_DoesNotPassThatHeaderOn()
    {
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(_Handler(_Coordinator(), upstream));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://depot.example.com/cockpit/inbox");
        request.Headers.Host = "evil.example.com";
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer plugin-chosen");

        using var _ = await client.SendAsync(request);

        var sent = Assert.Single(upstream.Requests);
        Assert.Null(sent.Host);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
    }

    [Fact]
    public async Task Send_WithoutAUsableCredential_Answers401WithoutContactingTheServer()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Depot, interactive: false, Arg.Any<CancellationToken>()).Returns(McpOAuthAccess.AuthorizationRequired);
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(_Handler(coordinator, upstream));

        using var response = await client.GetAsync("https://depot.example.com/cockpit/inbox");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Send_TheServerRefusesTheToken_RenewsItThroughTheCoordinatorAndSendsOnceMore()
    {
        var coordinator = _Coordinator();
        coordinator.RenewRejectedAsync(Depot, Token, Arg.Any<CancellationToken>()).Returns(McpOAuthAccess.Authorized(RenewedToken));
        var upstream = new _RecordingUpstream { RefuseFirst = true };
        using var client = new HttpClient(_Handler(coordinator, upstream));

        using var response = await client.GetAsync("https://depot.example.com/cockpit/inbox");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([$"Bearer {Token}", $"Bearer {RenewedToken}"], upstream.Requests.Select(sent => sent.Authorization));
    }

    // Through the host itself: a name another plugin contributed is not this plugin's to spend a credential on.
    [Fact]
    public async Task CreateAuthorizedHandler_AServerAnotherPluginContributed_IsRefused()
    {
        var other = new _OtherPluginProvider();
        var host = _Host(_Coordinator(), new _OwnProvider("https://depot.example.com/mcp"), other);
        using var client = new HttpClient(host.CreateAuthorizedHandler("Depot: Other"));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://other.example.com/cockpit/inbox"));
    }

    // A real listener and the host's own upstream: the bearer rides a WebSocket upgrade, which is how SignalR's
    // WebSockets transport uses HttpMessageHandlerFactory, and a redirect to elsewhere comes back unfollowed.
    [Fact]
    public async Task CreateAuthorizedHandler_AgainstARealServer_CarriesTheBearerOnAWebSocketUpgradeAndDoesNotFollowARedirect()
    {
        var seen = new List<string?>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/hubs/inbox", async context =>
        {
            seen.Add(context.Request.Headers.Authorization);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        app.MapGet("/elsewhere", context =>
        {
            seen.Add(context.Request.Headers.Authorization);
            context.Response.Redirect("http://localhost:9/stolen");
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var origin = app.Urls.Single();

        var host = _Host(_Coordinator(), new _OwnProvider($"{origin}/mcp"));
        using var handler = host.CreateAuthorizedHandler("Depot: Work");
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{origin.Replace("http", "ws", StringComparison.Ordinal)}/hubs/inbox"), invoker, CancellationToken.None);
        using var client = new HttpClient(handler, disposeHandler: false);
        using var redirect = await client.GetAsync($"{origin}/elsewhere");

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal([$"Bearer {Token}", $"Bearer {Token}"], seen);
    }

    private static PluginMcpAuthorizedHandler _Handler(IMcpOAuthCoordinator coordinator, HttpMessageHandler upstream) =>
        new(Depot.Name, () => Depot, coordinator, upstream);

    private static IMcpOAuthCoordinator _Coordinator()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), interactive: false, Arg.Any<CancellationToken>()).Returns(McpOAuthAccess.Authorized(Token));
        return coordinator;
    }

    private static PluginBackendHost _Host(IMcpOAuthCoordinator coordinator, _OwnProvider own, params IPluginMcpProvider[] others)
    {
        var services = new ServiceCollection().AddSingleton(coordinator).AddSingleton<IPluginMcpProvider>(own);
        foreach (var provider in others)
        {
            services.AddSingleton(provider);
        }

        return new PluginBackendHost(
            "depot",
            "Depot",
            services.BuildServiceProvider(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            Substitute.For<ICockpitActions>(),
            new PluginDiagnostics(),
            typeof(_OwnProvider));
    }

    // Every header and URI the plugin can read after the call, as one string to search for the token.
    private static string _EverythingThePluginSees(HttpRequestMessage request, HttpResponseMessage response) =>
        string.Join(
            "\n",
            request.RequestUri,
            request.Headers,
            response.Headers,
            response.Content.Headers,
            response.RequestMessage?.Headers,
            response.ToString());

    private sealed record _Sent(string? Authorization, string? PluginHeader, string? Host);

    private sealed class _RecordingUpstream : HttpMessageHandler
    {
        public List<_Sent> Requests { get; } = [];

        public bool RefuseFirst { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new _Sent(
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Plugin", out var values) ? values.Single() : null,
                request.Headers.Host));

            // Like SocketsHttpHandler, the response points back at the request it answers: here, the copy.
            var status = RefuseFirst && Requests.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }

    private sealed class _OwnProvider(string url) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new McpServerContribution("Depot: Work", url) { OAuthAuthority = new Uri(url).GetLeftPart(UriPartial.Authority) }];
    }

    private sealed class _OtherPluginProvider : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() =>
            [new McpServerContribution("Depot: Other", "https://other.example.com/mcp") { OAuthAuthority = "https://other.example.com" }];
    }
}
