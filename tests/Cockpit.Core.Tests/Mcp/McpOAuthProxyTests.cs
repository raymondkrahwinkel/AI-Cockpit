using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The loopback endpoint that stands in front of an OAuth-protected MCP server (AC-524), driven end to end: a real
/// Kestrel listener on each side and a real HTTP client in between.
/// <para>
/// Why it exists at all is a measurement: the Claude CLI reads its <c>--mcp-config</c> exactly once, when it opens
/// the connection, and never again — not after a 401, not across a reconnect, not after the file is rewritten or
/// deleted. Every design that leans on rewriting that file is therefore dead, and the only place left to put a fresh
/// credential is on the individual request.
/// </para>
/// </summary>
public class McpOAuthProxyTests
{
    private const string FreshToken = "freshly-renewed-access-token";

    private static McpServerConfig _ServerAt(string url) => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
    };

    private static IMcpOAuthCoordinator _CoordinatorAnswering(McpOAuthAccess access)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(access);
        return coordinator;
    }

    private static (McpOAuthProxyHost Proxy, McpAuthKey Key) _Proxy(IMcpOAuthCoordinator coordinator)
    {
        var key = new McpAuthKey();
        return (new McpOAuthProxyHost(coordinator, key, new SessionMcpKeyring(), NullLoggerFactory.Instance), key);
    }

    // Mounting has one answer a test can build on and one it cannot, so the check belongs here rather than repeated
    // at every call site as a nullability appeasement.
    private static async Task<string> _MountedAsync(McpOAuthProxyHost proxy, McpServerConfig server)
    {
        var url = await proxy.MountAsync(server);
        Assert.NotNull(url);
        return url;
    }

    private static HttpRequestMessage _Post(string url, string body, McpAuthKey key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Value);
        return request;
    }

    [Fact]
    public async Task AProxiedCall_ReachesTheServerWithAFreshTokenRatherThanTheLocalKey()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync(async (context, _) =>
        {
            context.Response.Headers["Mcp-Session-Id"] = "session-from-the-server";
            await context.Response.WriteAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        });

        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            var request = _Post(proxyUrl, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", key);
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", "session-from-the-agent");
            using var response = await client.SendAsync(request);

            // The credential the agent holds is the cockpit's local key; the credential the server sees is the OAuth
            // token, obtained for this request. That swap is the whole feature — it is what makes the token's
            // lifetime stop being the session's lifetime.
            Assert.Equal($"Bearer {FreshToken}", upstream.LastAuthorization);
            Assert.Equal("POST", upstream.LastMethod);
            Assert.Equal("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", upstream.LastBody);

            // MCP's own headers are the session's identity on this transport. Dropping one in either direction would
            // make every call look like a new session to whichever side lost it.
            Assert.Equal("session-from-the-agent", upstream.LastHeaders["Mcp-Session-Id"]);
            Assert.Equal("session-from-the-server", response.Headers.GetValues("Mcp-Session-Id").Single());
            Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":{}}""", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AProxiedCall_WithoutTheLocalKey_NeverReachesTheServer()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) => context.Response.WriteAsync("{}"));

        var (proxy, _) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.PostAsync(proxyUrl, new StringContent("{}"));

            // The listener sits on a loopback port any local process can find, and behind it is a credential for
            // somebody else's server. Without this run's key it is not a door at all.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(upstream.LastAuthorization);
            Assert.Null(upstream.LastMethod);
        }
    }

    [Fact]
    public async Task AnEventStream_ReachesTheAgentAsItIsWritten_NotWhenTheServerIsDone()
    {
        using var secondEventReleased = new SemaphoreSlim(0, 1);
        await using var upstream = await InProcessUpstreamServer.StartAsync(async (context, _) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();

            // The response deliberately does not end here. A proxy that reads the body to completion before writing
            // anything out would sit on this line for as long as the server keeps the stream open — which for MCP's
            // server-to-client stream is "as long as the session lasts".
            await secondEventReleased.WaitAsync(TimeSpan.FromSeconds(30));
            await context.Response.WriteAsync("data: second\n\n");
        });

        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var request = _Post(proxyUrl, """{"jsonrpc":"2.0","id":1,"method":"tools/call"}""", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            // Read while the server is still holding the stream open. This is the assertion: the first event has to
            // be readable before the response has ended, or an MCP session over SSE simply hangs.
            var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("data: first", first);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            secondEventReleased.Release();
            Assert.Equal(string.Empty, await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("data: second", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task WhenTheCredentialCannotBeRenewed_TheCallIsAnsweredAndTheServerStaysConnected()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) => context.Response.WriteAsync("{}"));

        var coordinator = _CoordinatorAnswering(
            McpOAuthAccess.AuthorizationRequired with { Reason = McpOAuthAttentionReason.SignInExpired });
        var (proxy, key) = _Proxy(coordinator);
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","id":7,"method":"tools/call"}""", key));
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            // A 401 is what makes the CLI drop the server and every tool on it for the rest of the session, with no
            // way back from inside that session — the exact failure this endpoint exists to prevent. So the call is
            // answered as a call, carrying the reason and the action, and the next one works the moment the operator
            // has signed in again.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(7, (int)body["id"]!);
            Assert.Equal(-32001, (int)body["error"]!["code"]!);
            Assert.Contains("press Sign in", (string)body["error"]!["message"]!, StringComparison.Ordinal);
            Assert.Null(upstream.LastMethod);
        }
    }

    [Fact]
    public async Task WhenTheServerItselfRefusesTheToken_ThatRefusalIsNotPassedOnAsA401()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", key));

            // A token the cockpit believes in and the server rejects is the same class of failure as one that could
            // not be renewed, and relaying the 401 would end the session's use of this server just as thoroughly.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(3, (int)body["id"]!);
        }
    }

    [Fact]
    public async Task WhenTheCredentialCannotBeRenewed_ANotificationIsAcknowledgedRatherThanAnswered()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) => context.Response.WriteAsync("{}"));

        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.AuthorizationRequired));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","method":"notifications/initialized"}""", key));

            // A notification carries no id and expects no reply, so there is nothing to answer. Inventing a response
            // for one would be a worse invention than the envelope above, which at least answers a real request.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
    }

    [Fact]
    public async Task MountingTheSameServerTwice_ReusesTheOneEndpoint()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) => context.Response.WriteAsync("{}"));

        var (proxy, _) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var first = await proxy.MountAsync(_ServerAt(upstream.Url));
            var second = await proxy.MountAsync(_ServerAt(upstream.Url));

            // The endpoint outlives the session that asked for it, so every later session finds the one already
            // listening. A listener per session start would leak a port and a Kestrel host per session.
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public async Task AServerThatIsNotOAuthProtected_GetsNoEndpointAtAll()
    {
        var (proxy, _) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.NotRequired));
        await using (proxy)
        {
            var apiKeyServer = _ServerAt("https://youtrack.example/mcp") with { Auth = McpServerAuth.ApiKey, ApiKey = "yt-pat" };
            var stdioServer = _ServerAt(string.Empty) with { Transport = McpTransport.Stdio, Command = "npx" };

            // The scope of this whole feature in one assertion: the eight cockpit-hosted servers and every API-key
            // server keep the address and the auth they already had, because nothing here has anything to offer them.
            Assert.Null(await proxy.MountAsync(apiKeyServer));
            Assert.Null(await proxy.MountAsync(stdioServer));
        }
    }
}
