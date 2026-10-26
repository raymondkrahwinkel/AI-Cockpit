using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
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

    // A plugin-set Host would steer SNI and the virtual host, and a plugin-set Authorization is not the host's.
    [Theory]
    [InlineData("https://depot.example.com/cockpit/inbox")]
    [InlineData("wss://depot.example.com/hubs/inbox?expectedUser=u1")]
    public async Task Send_ToItsOwnOrigin_PutsTheBearerOnTheWireAndNowhereThePluginCanSee(string target)
    {
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(_Handler(_Coordinator(), upstream));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.TryAddWithoutValidation("X-Plugin", "kept");
        request.Headers.Host = "evil.example.com";
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer plugin-chosen");

        using var response = await client.SendAsync(request);

        var sent = Assert.Single(upstream.Requests);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("kept", sent.PluginHeader);
        Assert.Null(sent.Host);
        Assert.DoesNotContain(Token, _EverythingThePluginSees(request, response), StringComparison.Ordinal);
        Assert.Same(request, response.RequestMessage);
    }

    // The last row repoints the plugin's own request while the credential is fetched (a TOCTOU on RequestUri).
    [Theory]
    [InlineData("https://evil.example.com/cockpit/inbox", null)]
    [InlineData("http://depot.example.com/cockpit/inbox", null)]
    [InlineData("https://depot.example.com:8443/cockpit/inbox", null)]
    [InlineData("wss://evil.example.com/hubs/inbox", null)]
    [InlineData("https://depot.example.com.evil.example.com/cockpit/inbox", null)]
    [InlineData("https://depot.example.com/cockpit/inbox", "https://evil.example.com/steal")]
    public async Task Send_TheBearerNeverLeavesTheServersOrigin(string target, string? repointTo)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), interactive: false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (repointTo is not null)
            {
                request.RequestUri = new Uri(repointTo);
            }

            return McpOAuthAccess.Authorized(Token);
        });
        var upstream = new _RecordingUpstream();
        using var invoker = new HttpMessageInvoker(_Handler(coordinator, upstream));

        var refusal = await Record.ExceptionAsync(() => invoker.SendAsync(request, CancellationToken.None));

        Assert.All(upstream.Requests, sent => Assert.Equal("depot.example.com", sent.Target?.Host));
        Assert.Equal(repointTo is null, refusal is HttpRequestException);
        Assert.DoesNotContain(Token, refusal?.ToString() ?? "", StringComparison.Ordinal);
    }

    // missing: no usable credential; refused: the server turns the token away; broken: fetching it throws with the token.
    [Theory]
    [InlineData("missing", HttpStatusCode.Unauthorized, new string[0])]
    [InlineData("refused", HttpStatusCode.OK, new[] { $"Bearer {Token}", $"Bearer {RenewedToken}" })]
    [InlineData("broken", null, new string[0])]
    public async Task Send_TheCredentialsState_DecidesWhatReachesTheServer(string state, HttpStatusCode? status, string[] sentAuthorizations)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Depot, interactive: false, Arg.Any<CancellationToken>()).Returns(_ => state switch
        {
            "missing" => McpOAuthAccess.AuthorizationRequired,
            "broken" => throw new InvalidOperationException($"token endpoint said {Token}"),
            _ => McpOAuthAccess.Authorized(Token),
        });
        coordinator.RenewRejectedAsync(Depot, Token, Arg.Any<CancellationToken>()).Returns(McpOAuthAccess.Authorized(RenewedToken));
        var upstream = new _RecordingUpstream { RefuseFirst = state == "refused" };
        using var client = new HttpClient(_Handler(coordinator, upstream));

        var outcome = await Record.ExceptionAsync(async () =>
        {
            using var response = await client.GetAsync("https://depot.example.com/cockpit/inbox");
            Assert.Equal(status, response.StatusCode);
        });

        Assert.Equal(sentAuthorizations, upstream.Requests.Select(sent => sent.Authorization));
        Assert.Equal(status is null, outcome is HttpRequestException);
        Assert.DoesNotContain(Token, outcome?.ToString() ?? "", StringComparison.Ordinal);
    }

    // Through the host and a real listener: the bearer rides a WebSocket upgrade (how SignalR's WebSockets transport
    // uses HttpMessageHandlerFactory), a redirect comes back unfollowed, a cookie the server set is not sent back,
    // and a server another plugin contributed is refused.
    [Fact]
    public async Task CreateAuthorizedHandler_AgainstARealServer_KeepsTheBearerOnItsOwnServersOrigin()
    {
        var seen = new List<string>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/hubs/inbox", async context =>
        {
            seen.Add($"{context.Request.Headers.Authorization}|cookie={context.Request.Headers.Cookie}");
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });
        app.MapGet("/elsewhere", context =>
        {
            seen.Add($"{context.Request.Headers.Authorization}|cookie={context.Request.Headers.Cookie}");
            context.Response.Headers.SetCookie = "session=abc";
            context.Response.Redirect("http://localhost:9/stolen");
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var origin = app.Urls.Single();

        var host = _Host(_Coordinator(), new _OwnProvider($"{origin}/mcp"), new _OtherPluginProvider());
        using var handler = host.CreateAuthorizedHandler("Depot: Work");
        using var client = new HttpClient(handler, disposeHandler: false);
        using var redirect = await client.GetAsync($"{origin}/elsewhere");
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{origin.Replace("http", "ws", StringComparison.Ordinal)}/hubs/inbox"), invoker, CancellationToken.None);
        using var other = new HttpClient(host.CreateAuthorizedHandler("Depot: Other"));

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal([$"Bearer {Token}|cookie=", $"Bearer {Token}|cookie="], seen);
        await Assert.ThrowsAsync<HttpRequestException>(() => other.GetAsync("https://other.example.com/cockpit/inbox"));
    }

    // AC-1586: the host-only token. One redemption for a burst, the rotated refresh token kept with the sign-in, the
    // session's own token left alone, and the narrow token on the wire only.
    [Fact]
    public async Task CreateAuthorizedHandler_WithAScope_SpendsAHostOnlyTokenTheSignInNeverHolds()
    {
        var store = Substitute.For<IMcpOAuthTokenStore>();
        McpOAuthToken? kept = new()
        {
            AccessToken = Token,
            RefreshToken = "refresh-1",
            Scope = "depot offline_access",
            ResourceUrl = Depot.Url,
            ClientId = "depot",
            AuthorizationServer = "https://depot.example.com",
        };
        store.GetAsync(Depot.IdentityKey, Arg.Any<CancellationToken>()).Returns(_ => kept);
        store.SaveAsync(Depot.IdentityKey, Depot.Name, Arg.Do<McpOAuthToken>(token => kept = token), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var endpoint = new _NarrowingTokenEndpoint();
        var hostOnly = new McpOAuthHostOnlyTokens(store, new McpOAuthScopeNarrower(NullLogger<McpOAuthScopeNarrower>.Instance, endpoint), new McpOAuthRefreshGate());
        var upstream = new _RecordingUpstream();
        using var client = new HttpClient(new PluginMcpAuthorizedHandler(
            Depot.Name,
            () => Depot with { OAuthHostOnlyScopes = "depot.inbox" },
            (server, cancellationToken) => hostOnly.AcquireAsync(server, "depot.inbox", rejected: null, cancellationToken),
            (server, rejected, cancellationToken) => hostOnly.AcquireAsync(server, "depot.inbox", rejected, cancellationToken),
            upstream));

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.GetAsync("https://depot.example.com/cockpit/inbox")));

        Assert.Equal(1, endpoint.Redemptions);
        Assert.All(upstream.Requests, sent => Assert.Equal("Bearer inbox-token", sent.Authorization));
        Assert.Equal((Token, "refresh-2"), (kept?.AccessToken, kept?.RefreshToken));
        Assert.All(responses, response => Assert.DoesNotContain("inbox-token", _EverythingThePluginSees(response.RequestMessage ?? new HttpRequestMessage(), response), StringComparison.Ordinal));
    }

    // AC-1586: the contract keeps the token in the host. No member of the plugin's host, nor of the handler it gets,
    // hands a plugin anything that reads as a credential.
    [Fact]
    public void ThePluginSurface_HasNoMemberThatReturnsACredential()
    {
        string[] credentialWords = ["token", "bearer", "credential", "secret", "authorization"];
        var hostMembers = new[] { typeof(ICockpitHost) }
            .Concat(typeof(ICockpitHost).GetInterfaces())
            .SelectMany(type => type.GetMembers())
            .Where(member => member is MethodInfo or PropertyInfo);
        var handlerMembers = typeof(PluginMcpAuthorizedHandler)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(member => member is not ConstructorInfo);

        var leaking = hostMembers
            .Where(member => _ReturnsText(member) && credentialWords.Any(word => member.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Concat(handlerMembers)
            .Select(member => $"{member.DeclaringType?.Name}.{member.Name}");

        Assert.Empty(leaking);
    }

    private static bool _ReturnsText(MemberInfo member)
    {
        var type = member switch
        {
            MethodInfo method => method.ReturnType,
            PropertyInfo property => property.PropertyType,
            _ => typeof(void),
        };
        return type == typeof(string)
            || (type.IsGenericType && type.GetGenericArguments().Any(argument => argument == typeof(string)));
    }

    private static PluginMcpAuthorizedHandler _Handler(IMcpOAuthCoordinator coordinator, HttpMessageHandler upstream) =>
        new(Depot.Name, () => Depot, coordinator, upstream);

    private static IMcpOAuthCoordinator _Coordinator()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), interactive: false, Arg.Any<CancellationToken>()).Returns(McpOAuthAccess.Authorized(Token));
        return coordinator;
    }

    private static PluginBackendHost _Host(IMcpOAuthCoordinator coordinator, _OwnProvider own, IPluginMcpProvider other) =>
        new(
            "depot",
            "Depot",
            new ServiceCollection()
                .AddSingleton(coordinator)
                .AddSingleton<IPluginMcpProvider>(own)
                .AddSingleton(other)
                .BuildServiceProvider(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            Substitute.For<ICockpitActions>(),
            new PluginDiagnostics(),
            typeof(_OwnProvider));

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

    private sealed record _Sent(string? Authorization, string? PluginHeader, string? Host, Uri? Target);

    private sealed class _RecordingUpstream : HttpMessageHandler
    {
        public List<_Sent> Requests { get; } = [];

        public bool RefuseFirst { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new _Sent(
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Plugin", out var values) ? values.Single() : null,
                request.Headers.Host,
                request.RequestUri));

            // Like SocketsHttpHandler, the response points back at the request it answers: here, the copy.
            var status = RefuseFirst && Requests.Count == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }

    // Depot's token endpoint after DEP-415, slow enough that a burst of requests arrives while one refresh is out.
    private sealed class _NarrowingTokenEndpoint : HttpMessageHandler
    {
        private int _redemptions;

        public int Redemptions => Volatile.Read(ref _redemptions);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = """{"token_endpoint":"https://depot.example.com/connect/token"}""";
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _redemptions);
                await Task.Delay(100, cancellationToken);
                body = """{"access_token":"inbox-token","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh-2","scope":"depot.inbox"}""";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
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
