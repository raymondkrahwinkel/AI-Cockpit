using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The auth gate itself (AC-40, AC-89, AC-790, AC-791), tested where it lives rather than through a mounted MCP
/// endpoint: what a request is stamped with is not visible in an MCP tool result, and it is the whole point of
/// AC-791's model — a remote caller is authorized as one named role (<see cref="NodeCallerIdentity.PaneId"/>) and
/// never as a session, while a local session keeps the pane the keyring minted its token for.
/// A plain and a TLS listener are stood up on loopback because the middleware discriminates on
/// <c>Request.IsHttps</c>, which is what tells the two trust boundaries apart; in the real host the TLS one is the
/// off-loopback listener (see <see cref="CockpitMcpEndpointHost"/>).
/// </summary>
public class McpAuthMiddlewareTests
{
    private const string NodeSecret = "test-node-shared-secret";

    // AC-89, unchanged by AC-791: the shared app key authorizes and names no session, so the in-process tool loop
    // and anything not yet on a per-session token keep the null identity the consent broker has always seen.
    [Fact]
    public async Task LoopbackListener_SharedAppKey_AuthorizesAndNamesNoSession()
    {
        var authKey = new McpAuthKey();
        await using var listeners = await _AuthGatedListeners.StartAsync(authKey, new SessionMcpKeyring(), NodeSecret);

        Assert.Equal("<none>", await listeners.WhoAmIAsync(listeners.LoopbackUrl, authKey.Value));
    }

    // AC-89, unchanged by AC-791: a live per-session token names its pane, and that is what reaches the tool.
    [Fact]
    public async Task LoopbackListener_SessionToken_StampsThePaneTheKeyringMintedItFor()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-42");
        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), keyring, NodeSecret);

        Assert.Equal("pane-42", await listeners.WhoAmIAsync(listeners.LoopbackUrl, token));

        // And a revoked one is nobody again — the keyring stays the only thing that decides, as before.
        keyring.Revoke("pane-42", token);
        using var response = await listeners.SendAsync(listeners.LoopbackUrl, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // AC-791, criterion 1: a caller on the node listener is authorized as the single remote-caller role and not as
    // a session. Non-null on purpose: the null it used to get is the in-process tool loop's identity, and sharing
    // that would have let a locally remembered consent cover a call from another machine.
    [Fact]
    public async Task NodeListener_SharedSecret_StampsTheRemoteCallerRole_NotASession()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-42");
        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), keyring, NodeSecret);

        Assert.Equal(NodeCallerIdentity.PaneId, await listeners.WhoAmIAsync(listeners.NodeUrl, NodeSecret));
        Assert.NotEqual("pane-42", NodeCallerIdentity.PaneId);

        // The session's own token is a loopback credential and stays one (AC-790's boundary): presenting it here
        // is refused, so no remote caller can borrow a pane's identity by replaying its token.
        using var response = await listeners.SendAsync(listeners.NodeUrl, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // AC-791, criterion 3: a refusal has to be classifiable at the other end. "Server unreachable" carries no HTTP
    // status at all and "that tool does not exist" comes back 200 with a JSON-RPC error, so a 401 naming a token
    // problem in a machine-readable code is enough to tell all three apart.
    [Fact]
    public async Task NodeListener_Refusal_Carries401WithAReasonACallerCanTellFromSilence()
    {
        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), new SessionMcpKeyring(), NodeSecret);

        using var response = await listeners.SendAsync(listeners.NodeUrl, "not-a-valid-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Contains("invalid_token", Assert.Single(response.Headers.WwwAuthenticate).Parameter);
        Assert.Contains("invalid_token", await response.Content.ReadAsStringAsync());

        // Missing and wrong get the identical answer on purpose: the refusal must not become an oracle for which
        // of the two it was.
        using var noCredential = await listeners.SendAsync(listeners.NodeUrl, bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, noCredential.StatusCode);
        Assert.Equal(await response.Content.ReadAsStringAsync(), await noCredential.Content.ReadAsStringAsync());
    }

    // AC-791, criterion 4: and the loopback refusal is left exactly as it was — a bare 401 with no challenge. A
    // `WWW-Authenticate: Bearer` here is what an MCP client reads as "this server wants OAuth", so a local session
    // whose token was just revoked would be sent looking for a discovery document that does not exist. The reason
    // is for the controller, which has no other way to tell a refusal from an unreachable machine.
    [Fact]
    public async Task LoopbackListener_Refusal_StaysTheBare401ItAlwaysWas()
    {
        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), new SessionMcpKeyring(), NodeSecret);

        using var response = await listeners.SendAsync(listeners.LoopbackUrl, "not-a-valid-credential");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// One <see cref="WebApplication"/> behind <see cref="McpAuthMiddleware"/> with both listener kinds and a
    /// single endpoint that answers with whatever identity the middleware stamped — the one thing an MCP tool
    /// result cannot show.
    /// </summary>
    private sealed class _AuthGatedListeners(WebApplication app, HttpClient client, NodeSelfSignedCertificate certificate, string loopbackUrl, string nodeUrl) : IAsyncDisposable
    {
        public string LoopbackUrl { get; } = loopbackUrl;

        public string NodeUrl { get; } = nodeUrl;

        public static async Task<_AuthGatedListeners> StartAsync(McpAuthKey authKey, SessionMcpKeyring keyring, string? nodeSharedSecret)
        {
            // AC-792: the node's certificate now lives in a file, so the test gets one of its own rather than the
            // real cockpit's — and a live secret holder rather than a captured string, which is what the
            // middleware reads. Held for the listener's lifetime, not this method's: Kestrel keeps using it, so
            // disposing it here would break every TLS handshake that follows.
            var certificate = new NodeSelfSignedCertificate(Path.Combine(Path.GetTempPath(), $"auth-middleware-{Guid.NewGuid():N}.pfx"));
            var liveSecret = new NodeSharedSecret();
            liveSecret.Set(nodeSharedSecret);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(certificate.Value));
            });

            var app = builder.Build();
            McpAuthMiddleware.Require(app, authKey, keyring, liveSecret);
            app.MapGet("/whoami", () => McpRequestContext.CurrentPaneId ?? "<none>");
            await app.StartAsync();

            // The node listener's certificate is self-signed by design (see NodeSelfSignedCertificate) — a real
            // client is told out-of-band to trust this instance's; the test stands in for that trust.
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
            };

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            return new _AuthGatedListeners(
                app,
                new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
                certificate,
                addresses.Single(address => address.StartsWith("http://", StringComparison.Ordinal)) + "/whoami",
                addresses.Single(address => address.StartsWith("https://", StringComparison.Ordinal)) + "/whoami");
        }

        public async Task<string> WhoAmIAsync(string url, string bearer)
        {
            using var response = await SendAsync(url, bearer);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<HttpResponseMessage> SendAsync(string url, string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            certificate.Dispose();
        }
    }
}
