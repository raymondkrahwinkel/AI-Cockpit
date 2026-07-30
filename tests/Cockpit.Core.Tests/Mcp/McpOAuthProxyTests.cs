using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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

    private const string RenewedToken = "the-token-after-the-refusal";

    private static IMcpOAuthCoordinator _CoordinatorAnswering(McpOAuthAccess access)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(access);
        return coordinator;
    }

    /// <summary>
    /// Hands out <see cref="FreshToken"/> until the server refuses it, and <see cref="RenewedToken"/> after — the
    /// real coordinator's behaviour on this path, narrowed to what the forwarder can observe. Counts the renewals,
    /// because "exactly one" is the claim.
    /// </summary>
    private static IMcpOAuthCoordinator _CoordinatorThatRenewsOnRejection(McpOAuthAccess? renewalAnswer = null)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var handedOut = FreshToken;
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => McpOAuthAccess.Authorized(Volatile.Read(ref handedOut)));
        coordinator.RenewRejectedAsync(Arg.Any<McpServerConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Volatile.Write(ref handedOut, RenewedToken);
                return renewalAnswer ?? McpOAuthAccess.Authorized(RenewedToken);
            });

        return coordinator;
    }

    private static (McpOAuthProxyHost Proxy, McpAuthKey Key) _Proxy(IMcpOAuthCoordinator coordinator, ILoggerFactory? loggerFactory = null)
    {
        var key = new McpAuthKey();
        return (new McpOAuthProxyHost(coordinator, key, new SessionMcpKeyring(), loggerFactory ?? NullLoggerFactory.Instance), key);
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

            // The deadline goes on the send, not only on the reads. A proxy that buffers the whole upstream response
            // holds its own response headers back until the server is done — so the reads below would still succeed,
            // just half a minute late, and a test that timed only the reads would call that streaming.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream);

            // Read while the server is still holding the stream open. This is the assertion: the first event has to
            // be readable before the response has ended, or an MCP session over SSE simply hangs.
            var first = await reader.ReadLineAsync(deadline.Token);
            Assert.Equal("data: first", first);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            secondEventReleased.Release();
            Assert.Equal(string.Empty, await reader.ReadLineAsync(deadline.Token));
            Assert.Equal("data: second", await reader.ReadLineAsync(deadline.Token));
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
    public async Task WhenTheServerRefusesTheToken_TheCredentialIsRenewedAndTheCallGoesThroughOnTheSecondTry()
    {
        var seen = new List<string?>();
        await using var upstream = await InProcessUpstreamServer.StartAsync(async (context, server) =>
        {
            seen.Add(server.LastAuthorization);
            if (server.LastAuthorization != $"Bearer {RenewedToken}")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await context.Response.WriteAsync("""{"jsonrpc":"2.0","id":3,"result":{"tools":[]}}""");
        });

        var coordinator = _CoordinatorThatRenewsOnRejection();
        var (proxy, key) = _Proxy(coordinator);
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", key));

            // The cockpit judges a token on its own clock; only the server knows for certain. A grant revoked at the
            // far end, or a rotation race lost to another session, leaves something that looks healthy here and is
            // dead there — and answering that with a message instead of a renewal would leave every later call
            // presenting the same dead token, which is the server gone for the rest of the session.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("""{"jsonrpc":"2.0","id":3,"result":{"tools":[]}}""", await response.Content.ReadAsStringAsync());
            Assert.Equal([$"Bearer {FreshToken}", $"Bearer {RenewedToken}"], seen);
            Assert.Equal("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", upstream.LastBody);
            await coordinator.Received(1).RenewRejectedAsync(Arg.Any<McpServerConfig>(), FreshToken, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task WhenTheServerRefusesEvenAFreshToken_ItStopsAtOneRetryAndAnswersTheCall()
    {
        var attempts = 0;
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) =>
        {
            Interlocked.Increment(ref attempts);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        var coordinator = _CoordinatorThatRenewsOnRejection();
        var (proxy, key) = _Proxy(coordinator);
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", key));

            // Exactly one retry, never a loop: a server that refuses everything must cost two round trips per call
            // rather than a storm, and a second renewal would only find the same answer as the first.
            Assert.Equal(2, attempts);
            await coordinator.Received(1).RenewRejectedAsync(Arg.Any<McpServerConfig>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

            // And the refusal still never reaches the agent as a 401, which is what would take the server and every
            // tool on it out of the session for good.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.Equal(3, (int)body["id"]!);
            Assert.Equal(-32001, (int)body["error"]!["code"]!);
        }
    }

    [Fact]
    public async Task WhenTheRenewalAfterARefusalFails_TheCallIsAnsweredWithThatReason_NotTheRefusal()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync((context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        });

        var coordinator = _CoordinatorThatRenewsOnRejection(
            McpOAuthAccess.AuthorizationRequired with { Reason = McpOAuthAttentionReason.ServerUnreachable });
        var (proxy, key) = _Proxy(coordinator);
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var response = await client.SendAsync(_Post(proxyUrl, """{"jsonrpc":"2.0","id":9,"method":"tools/list"}""", key));
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            // The renewal is the thing that failed, so its reason is the one worth relaying — and it is not the same
            // advice as a refusal. The retry is not attempted with a credential that was never obtained.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("could not be reached", (string)body["error"]!["message"]!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AStreamThatStaysSilentForAWhile_IsNotCutOff()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync(async (context, _) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.Body.FlushAsync();

            // An MCP server-to-client stream says nothing at all until the server has something to send, which can
            // be minutes. This is the shape the whole ticket rests on, so it is pinned rather than assumed: nothing
            // in this chain — the proxy's own client above all, whose default hundred-second timeout would have
            // ended it — may treat silence as a dead connection.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await context.Response.WriteAsync("data: after the silence\n\n");
        });

        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)));
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var request = _Post(proxyUrl, """{"jsonrpc":"2.0","id":1,"method":"tools/call"}""", key);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream);

            Assert.Equal("data: after the silence", await reader.ReadLineAsync(deadline.Token));
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
    public async Task WhenTheServerBreaksOffMidStream_ThatIsHandledAndSaidRatherThanEscaping()
    {
        await using var upstream = await InProcessUpstreamServer.StartAsync(async (context, _) =>
        {
            // Promises far more than it delivers and then stops. The far side dying while its answer is already
            // going out is the one departure on this path that cannot be answered, because the response headers are
            // long gone — and this is the deterministic way to stage it. Aborting the connection outright would
            // stage it too, but it also tears down the agent's side, and then which of the two the proxy notices
            // first is a race: "the agent hung up" is silent by design, so the test would be asserting a coin flip.
            context.Response.Headers.ContentLength = 5000;
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
        });

        var logs = new CapturingLoggerFactory();
        var (proxy, key) = _Proxy(_CoordinatorAnswering(McpOAuthAccess.Authorized(FreshToken)), logs);
        await using (proxy)
        {
            var proxyUrl = await _MountedAsync(proxy, _ServerAt(upstream.Url));

            using var client = new HttpClient();
            using var request = _Post(proxyUrl, """{"jsonrpc":"2.0","id":1,"method":"tools/call"}""", key);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(deadline.Token) is not null)
                {
                }
            }
            catch (HttpRequestException)
            {
                // Expected on this side: the stream it was reading was cut. What is under test is the other side.
            }
            catch (IOException)
            {
            }

            // Every other departure in the forwarder is caught and logged. This one used to be the exception, and
            // its failure shape — a connection that simply drops — is exactly what the CLI reads as "server gone",
            // so it is the one that most needs a line saying what happened.
            Assert.True(await logs.WaitForAsync(
                entry => entry.Level == LogLevel.Warning && entry.Message.Contains("broke off", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)));
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
