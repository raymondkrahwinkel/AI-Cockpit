using System.Net;
using System.Text.Json;
using System.Web;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Core.Tests.Mcp;

// AC-1571 B2: a plugin's MCP server signed in on the server, its browser step on the desktop. Drives the real SDK flow
// against a PKCE-enforcing authorization server: the desktop's loopback catches the redirect, the server redeems it.
public class McpServerSignInHandoffTests
{
    [Fact]
    public async Task ASignInOnTheServer_TakesOnlyItsOwnRedirectFromTheDesktop_AndTheTokenNeverLeavesTheServer()
    {
        await using var oauth = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var logs = new CapturingLoggerFactory();
        var store = new FakeMcpOAuthTokenStore();
        var authorizer = new McpOAuthAuthorizer(logs.CreateLogger<McpOAuthAuthorizer>(), store)
        {
            BrowserOpener = _ => throw new InvalidOperationException("The server opened a browser of its own."),
        };
        var coordinator = new McpOAuthCoordinator(store, authorizer, logs.CreateLogger<McpOAuthCoordinator>());
        var server = new McpServerConfig { Id = "depot-work", Name = "Depot: Work", Transport = McpTransport.Http, Url = oauth.Url, Auth = McpServerAuth.OAuth };
        using var loopback = McpOAuthLoopback.Open(logs.CreateLogger("desktop")) ?? throw new InvalidOperationException("No loopback port opened.");
        using var browser = new HttpClient();
        var said = new List<string>();

        var signIn = await new McpServerSignIns(logs.CreateLogger<McpServerSignIns>()).StartAsync(
            "depot",
            server.Name,
            loopback.Port,
            async ct => (await coordinator.AcquireAsync(server, interactive: true, ct)).State == McpAuthState.Authorized
                ? PluginMcpSignInOutcome.Authorized
                : PluginMcpSignInOutcome.Declined,
            ended =>
            {
                said.Add($"{ended.FolderId}: {ended.Name} sign-in {ended.Status}");
                return Task.CompletedTask;
            },
            timeout.Token);
        said.Add(JsonSerializer.Serialize(PluginEndpoints.McpSignInView(signIn)));
        var link = signIn.Url ?? throw new InvalidOperationException("The server built no authorization link.");
        var state = McpOAuthLoopback.StateOf(link) ?? throw new InvalidOperationException("The link carries no state.");

        var receiving = loopback.ReceiveAsync(server.Name, state, timeout.Token);
        (await browser.GetAsync(link, timeout.Token)).EnsureSuccessStatusCode();
        var redirect = await receiving ?? throw new InvalidOperationException("The desktop's loopback caught no redirect.");
        var code = HttpUtility.ParseQueryString(redirect)["code"] ?? "";
        var codeAlone = await _RedeemWithoutVerifierAsync(browser, oauth, code, loopback.RedirectUri, timeout.Token);

        Assert.Equal(loopback.RedirectUri.AbsoluteUri, HttpUtility.ParseQueryString(link.Query)["redirect_uri"]);
        Assert.Equal(HttpStatusCode.BadRequest, codeAlone);
        Assert.False(signIn.Submit($"code={code}&state=another-request"), "A redirect for another request was taken.");
        Assert.True(signIn.AwaitsRedirect, "A refused redirect ended the sign-in.");
        Assert.True(signIn.Submit(redirect), "The sign-in's own redirect was refused.");
        await signIn.Ended.WaitAsync(timeout.Token);
        said.Add(JsonSerializer.Serialize(PluginEndpoints.McpSignInView(signIn)));

        Assert.Equal("succeeded", signIn.Status);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, (await store.GetAsync(server.IdentityKey))?.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, await _RedeemWithoutVerifierAsync(browser, oauth, code, loopback.RedirectUri, timeout.Token));
        var everything = string.Join("\n", said.Concat(logs.Entries.Select(entry => entry.Message)));
        Assert.DoesNotContain(InProcessOAuthMcpServer.AccessToken, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(InProcessOAuthMcpServer.RefreshToken, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(code, everything, StringComparison.Ordinal);
    }

    // What anyone holding only the code can do with it: the token endpoint, without the verifier the server kept.
    private static async Task<HttpStatusCode> _RedeemWithoutVerifierAsync(HttpClient http, InProcessOAuthMcpServer oauth, string code, Uri redirectUri, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["client_id"] = InProcessOAuthMcpServer.ClientId,
        });
        using var answer = await http.PostAsync($"{oauth.BaseUrl}/connect/token", form, cancellationToken);
        return answer.StatusCode;
    }
}
