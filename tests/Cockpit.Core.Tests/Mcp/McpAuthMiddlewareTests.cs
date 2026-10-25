using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
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

    // L2b (authorization), AC-1685 criterion 1, folding in AC-89: a session token is a Session even when the request
    // claims to be the assistant, only the assistant's own token reads as Assistant, and a revoked token is nobody.
    [Fact]
    public async Task LoopbackListener_SessionToken_IsASession_EvenWhenItClaimsToBeTheAssistant()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-42");
        var assistantToken = keyring.TokenFor(AssistantIdentity.PaneId);
        var mounts = _Mounting("pane-42");
        mounts.Grant(AssistantIdentity.PaneId, ["cockpit-probe"]);
        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), keyring, NodeSecret, mounts);

        Assert.Equal("pane-42", await listeners.WhoAmIAsync(listeners.LoopbackUrl, token));
        Assert.Equal("Session|pane-42|", await listeners.CallerAsync(listeners.LoopbackUrl, token, claim: AssistantIdentity.PaneId));
        Assert.Equal($"Assistant|{AssistantIdentity.PaneId}|", await listeners.CallerAsync(listeners.LoopbackUrl, assistantToken));

        keyring.Revoke("pane-42", token);
        using var response = await listeners.SendAsync(listeners.LoopbackUrl, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // L2b (authorization), AC-1685 criterion 2, folding in AC-791 criterion 1: a connect key is Remote with its
    // label and the pairing Remote without one, while the same session's token is refused there and is a Session
    // on loopback, so no remote caller can borrow a pane's identity by replaying its token.
    [Fact]
    public async Task NodeListener_ConnectKey_IsRemoteWithItsLabel_WhileTheSameSessionLocallyIsASession()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"auth-middleware-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var audit = new NodeAccessAuditLog(Path.Combine(directory, "node-access.jsonl"), NullLogger<NodeAccessAuditLog>.Instance);
            var connectKeys = new ConnectKeyVerifier(Path.Combine(directory, "cockpit.json"), _ => null, _ => { }, TimeProvider.System, audit, NullLogger.Instance);
            var issuer = new NodeCaller("testtest", "", ConnectKeyCapability.Admin, "127.0.0.1", CancellationToken.None);
            var (_, secret) = await connectKeys.IssueAsync("laptop", ConnectKeyCapability.Operate, 30, issuer);
            var keyring = new SessionMcpKeyring();
            var token = keyring.TokenFor("pane-42");
            await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), keyring, NodeSecret, _Mounting("pane-42"), connectKeys: connectKeys);

            Assert.Equal($"Remote|{NodeCallerIdentity.PaneId}|laptop", await listeners.CallerAsync(listeners.NodeUrl, secret));
            Assert.Equal($"Remote|{NodeCallerIdentity.PaneId}|", await listeners.CallerAsync(listeners.NodeUrl, NodeSecret));
            Assert.Equal("Session|pane-42|", await listeners.CallerAsync(listeners.LoopbackUrl, token));

            using var response = await listeners.SendAsync(listeners.NodeUrl, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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

    // AC-1148, the loopback negative control that never existed: a live token, correctly stamped — and an endpoint
    // this pane's launch never mounted. The old tests only ever asked "valid or not", which a wrong-scope call
    // passes; this is the case the design of those tests could not see.
    [Fact]
    public async Task LoopbackListener_SessionTokenForAnEndpointItsLaunchNeverMounted_Is403BeforeAnyTool()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-42");
        var mounts = new SessionMcpMounts();
        mounts.Grant("pane-42", ["cockpit-session", "cockpit-agents"]);

        await using var listeners = await _AuthGatedListeners.StartAsync(new McpAuthKey(), keyring, NodeSecret, mounts, serverName: "cockpit-shell");

        using var response = await listeners.SendAsync(listeners.LoopbackUrl, token);

        // 403 and not 401: the credential is fine and the cockpit says so, which is the whole difference between
        // "I do not know you" and "you may not be here". The endpoint answered before /whoami ever ran.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("forbidden", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // AC-1148: the measured one. `ShellAccess.Enabled = false` in the operator's own cockpit.json, and run_command
    // ran anyway — because the switch only decided what a session was told about, never what the listener took.
    // A pane that did mount the endpoint still gets nothing while the master switch is off.
    [Fact]
    public async Task LoopbackListener_EndpointTheOperatorSwitchedOff_Is403EvenForThePaneThatMountedIt()
    {
        var keyring = new SessionMcpKeyring();
        var token = keyring.TokenFor("pane-42");

        await using var listeners = await _AuthGatedListeners.StartAsync(
            new McpAuthKey(), keyring, NodeSecret, _Mounting("pane-42", "cockpit-shell"), serverName: "cockpit-shell", isEnabled: false);

        using var response = await listeners.SendAsync(listeners.LoopbackUrl, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // AC-1148 (was AC-1158): the node's own negative control. The secret is right — it is the pairing that grants
    // nothing, which is the normal state of a fresh one (AC-794), and an endpoint is not a thing you reach on
    // authentication alone.
    [Fact]
    public async Task NodeListener_ValidSecretButAPairingWithAnEmptyScope_Is403()
    {
        await using var listeners = await _AuthGatedListeners.StartAsync(
            new McpAuthKey(), new SessionMcpKeyring(), NodeSecret, nodeScopeGranted: false);

        using var response = await listeners.SendAsync(listeners.NodeUrl, NodeSecret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // AC-1148: and the master switch counts for the controller too — `bindNodeListener` now withholds the socket
    // outright (see CockpitInternalMcpProviderTests), so this is the belt to that braces: were one ever bound, a
    // switched-off endpoint still refuses.
    [Fact]
    public async Task NodeListener_EndpointTheOperatorSwitchedOff_Is403EvenWithAScopedPairing()
    {
        await using var listeners = await _AuthGatedListeners.StartAsync(
            new McpAuthKey(), new SessionMcpKeyring(), NodeSecret, isEnabled: false);

        using var response = await listeners.SendAsync(listeners.NodeUrl, NodeSecret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // AC-856: right secret, scoped pairing, and still no endpoint that was not built to face a controller. The
    // grant used to be a boolean — one ticked profile answered 200 on every enabled non-Internal endpoint, a shell
    // among them, because AC-794's per-profile checks live in NodeSessionMcpTools and narrow nothing elsewhere.
    [Fact]
    public async Task NodeListener_AnEndpointThatIsNotNodeOnly_Is403EvenWithAScopedPairing()
    {
        await using var listeners = await _AuthGatedListeners.StartAsync(
            new McpAuthKey(), new SessionMcpKeyring(), NodeSecret, serverName: "cockpit-shell", nodeOnly: false);

        using var response = await listeners.SendAsync(listeners.NodeUrl, NodeSecret);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static SessionMcpMounts _Mounting(string paneId, string serverName = "cockpit-probe")
    {
        var mounts = new SessionMcpMounts();
        mounts.Grant(paneId, [serverName]);
        return mounts;
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

        public static async Task<_AuthGatedListeners> StartAsync(
            McpAuthKey authKey,
            SessionMcpKeyring keyring,
            string? nodeSharedSecret,
            SessionMcpMounts? mounts = null,
            string serverName = "cockpit-probe",
            bool isEnabled = true,
            bool nodeScopeGranted = true,
            // AC-856: only a NodeOnly endpoint has a node listener, so that is what this harness stands in for.
            bool nodeOnly = true,
            ConnectKeyVerifier? connectKeys = null)
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

            // AC-1148: the real policy, wired the way both hosts wire it — so what these tests exercise is the
            // decision itself and not a stand-in that happens to agree with it.
            var grants = mounts ?? new SessionMcpMounts();
            McpAuthMiddleware.Require(
                app,
                authKey,
                keyring,
                paneId => new ValueTask<bool>(McpEndpointAuthorization.Allows(paneId, serverName, isEnabled, nodeScopeGranted, nodeOnly, grants)),
                liveSecret,
                connectKeys);
            app.MapGet("/whoami", () => McpRequestContext.CurrentPaneId ?? "<none>");

            // AC-1685: the caller a plugin would read; the query string is the tool argument a caller controls.
            app.MapGet("/caller", (string? claim) => McpRequestContext.CurrentCaller is { } caller ? $"{caller.Kind}|{caller.PaneId}|{caller.ViaConnectKeyLabel}" : "<none>");
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

        public Task<string> CallerAsync(string whoAmIUrl, string bearer, string? claim = null) =>
            WhoAmIAsync(whoAmIUrl[..^"/whoami".Length] + "/caller" + (claim is null ? "" : $"?claim={Uri.EscapeDataString(claim)}"), bearer);

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
