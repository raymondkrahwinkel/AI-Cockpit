using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

// AC-1511, layer 2: a sign-in without a browser. The redeemer finds the token endpoint the way the SDK does, redeems
// the grant once, and files tokens the SDK can renew later; a refused or unanswered grant stores nothing, and no log
// line carries the grant or a token.
public class McpOAuthGrantRedeemerTests
{
    [Theory]
    [InlineData("a fresh token", InProcessOAuthMcpServer.InstallToken, 0, PluginMcpSignInOutcome.Authorized)]
    [InlineData("a token already redeemed", InProcessOAuthMcpServer.InstallToken, 1, PluginMcpSignInOutcome.Declined)]
    [InlineData("a token Depot never issued", "another-install-token", 0, PluginMcpSignInOutcome.Declined)]
    [InlineData("a server that does not answer", InProcessOAuthMcpServer.InstallToken, 0, PluginMcpSignInOutcome.Unreachable)]
    public async Task RedeemAsync_SignsInOnceWithoutABrowser_AndTheTokensItKeepsRenewThemselves(
        string row, string installToken, int priorRedemptions, PluginMcpSignInOutcome expected)
    {
        await using var depot = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var url = row == "a server that does not answer" ? "http://127.0.0.1:9/mcp" : depot.Url;
        var server = new McpServerConfig { Id = "depot-1", Name = "Depot: Work", Transport = McpTransport.Http, Url = url, Auth = McpServerAuth.OAuth };
        var parameters = new Dictionary<string, string> { ["install_token"] = installToken, ["client_id"] = "depot" };
        var store = new FakeMcpOAuthTokenStore();
        var logger = new CapturingLogger<McpOAuthGrantRedeemer>();
        var redeemer = new McpOAuthGrantRedeemer(store, logger);
        for (var redeemed = 0; redeemed < priorRedemptions; redeemed++)
        {
            await new McpOAuthGrantRedeemer(new FakeMcpOAuthTokenStore(), NullLogger<McpOAuthGrantRedeemer>.Instance)
                .RedeemAsync(server, "urn:depot:grant-type:install_token", parameters, CancellationToken.None);
        }

        var outcome = await redeemer.RedeemAsync(server, "urn:depot:grant-type:install_token", parameters, CancellationToken.None);
        var stored = await store.GetAsync("depot-1");

        Assert.Equal(expected, outcome);
        Assert.False(logger.Messages.Any(message => message.Contains("test-", StringComparison.Ordinal)), "A log line carries the grant or a token.");
        if (expected != PluginMcpSignInOutcome.Authorized)
        {
            Assert.Null(stored);
            return;
        }

        Assert.NotNull(stored);
        var kept = stored;
        Assert.Equal((InProcessOAuthMcpServer.AccessToken, InProcessOAuthMcpServer.RefreshToken), (kept.AccessToken, kept.RefreshToken));
        Assert.Equal(("depot", depot.BaseUrl, depot.Url), (kept.ClientId, kept.AuthorizationServer, kept.ResourceUrl));

        // What makes it a sign-in rather than an hour of access: the SDK renews it later with the client it was kept for.
        await store.SaveAsync("depot-1", server.Name, kept with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        var coordinator = new McpOAuthCoordinator(store, new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store), NullLogger<McpOAuthCoordinator>.Instance);
        var renewed = await coordinator.AcquireAsync(server, interactive: false);
        Assert.Equal((McpAuthState.Authorized, InProcessOAuthMcpServer.RenewedAccessToken), (renewed.State, renewed.AccessToken));
    }
}
