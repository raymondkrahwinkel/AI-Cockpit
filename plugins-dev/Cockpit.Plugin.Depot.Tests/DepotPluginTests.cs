extern alias UiAsm;

using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotPlugin`'s `Initialize` plus its `IPluginMcpProvider` half (AC-504). Asserts on
// the registration's/contribution's content, not merely that a host method was called: a call with the wrong
// scheme, URL or a blank instruction would still "pass" a test that only checked the call happened.
public class DepotPluginTests
{
    private static ICockpitHost _HostWithConnections(params DepotConnectionRegistration[] connections)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        if (connections.Length > 0)
        {
            new Settings.DepotSettings(host.Storage) { Connections = connections };
        }

        return host;
    }

    [Fact]
    public void GetMcpServers_AfterTwoConnectionsSwapNames_EachKeepsItsOwnId_AndEachConnectionCarriesSettings()
    {
        // Acceptance criterion 3, the Depot half. Two connections on the same host swap names. If identity
        // followed the name, each would inherit the other's token — McpOAuthToken.IsForResource only bounds
        // a token to scheme/host/port, so one connection would present the other's bearer to a wrong endpoint.
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "alpha", "https://depot.example.com/alpha"),
            new DepotConnectionRegistration("c2", "beta", "https://depot.example.com/beta")));

        var before = plugin.GetMcpServers();

        using var afterSwap = new DepotPlugin();
        afterSwap.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "beta", "https://depot.example.com/alpha"),
            new DepotConnectionRegistration("c2", "alpha", "https://depot.example.com/beta")));

        var after = afterSwap.GetMcpServers();

        // The names swapped; the ids did not follow them.
        Assert.Equal("c1", Assert.Single(before, server => server.Name == "Depot: alpha").Id);
        Assert.Equal("c1", Assert.Single(after, server => server.Name == "Depot: beta").Id);
        Assert.Equal("c2", Assert.Single(before, server => server.Name == "Depot: beta").Id);
        Assert.Equal("c2", Assert.Single(after, server => server.Name == "Depot: alpha").Id);

        var selectedHost = _HostWithConnections(
            new DepotConnectionRegistration("b", "B", "https://b.example", SynchroniseSettings: true),
            new DepotConnectionRegistration("a", "A", "https://a.example", SynchroniseSettings: true));
        var carriers = new List<Cockpit.Plugins.Abstractions.Settings.SettingsCarrierRegistration>();
        selectedHost.When(candidate => candidate.AddSettingsCarrier(Arg.Any<Cockpit.Plugins.Abstractions.Settings.SettingsCarrierRegistration>()))
            .Do(call => carriers.Add(call.Arg<Cockpit.Plugins.Abstractions.Settings.SettingsCarrierRegistration>()));
        using var selectedPlugin = new DepotPlugin();
        selectedPlugin.Initialize(selectedHost);

        Assert.Equal(["depot:b", "depot:a"], carriers.Select(carrier => carrier.Id));

        var optedOutHost = _HostWithConnections(new DepotConnectionRegistration("c", "C", "https://c.example", SynchroniseSettings: false));
        using var optedOutPlugin = new DepotPlugin();
        optedOutPlugin.Initialize(optedOutHost);

        optedOutHost.DidNotReceive().AddSettingsCarrier(Arg.Any<Cockpit.Plugins.Abstractions.Settings.SettingsCarrierRegistration>());
    }

    // AC-1647, layer 2: only Depot's built-in Cockpit sign-in receives the host scope used by profile and backups;
    // host-only narrowing keeps that scope off every agent-session token.
    [Fact]
    public void GetMcpServers_BuiltInSignInRequestsDepotHostAndKeepsItHostOnly_AndOAuthAuthorityIsTheOriginNotTheSubpath()
    {
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://host.example.com/depot")));

        var server = Assert.Single(plugin.GetMcpServers("project-a", ["depot"]));

        Assert.Equal("depot depot.inbox depot.host offline_access", server.OAuthScopes);
        Assert.Equal("depot.inbox depot.host", server.OAuthHostOnlyScopes);
        // AC-499: OAuthAuthority is the origin (scheme+host+port) of the normalized URL, not the stored URL's own path -
        // a subpath deployment's authority lives at the origin, not under the subpath.
        Assert.Equal("https://host.example.com/depot/mcp", server.Url);
        Assert.Equal("https://host.example.com", server.OAuthAuthority);
    }

    // AC-1511, layer 2: a connection signed in with an installation token. The grant goes to the host with Depot's own
    // client, the operator reads one sentence per outcome, and a token Depot refuses leaves no connection behind.
    [Theory]
    [InlineData(PluginMcpSignInOutcome.Authorized, "Signed in.", 1)]
    [InlineData(PluginMcpSignInOutcome.Declined, DepotPlugin.InvalidInstallTokenMessage, 0)]
    [InlineData(PluginMcpSignInOutcome.Unreachable, "Couldn't reach https://depot.example.com to sign in. Check the address and try again.", 0)]
    public async Task ConnectWithInstallToken_RedeemsTheTokenWithDepotsClient_AndARefusedTokenLeavesNoConnection(
        PluginMcpSignInOutcome outcome, string message, int connections)
    {
        const string token = "install-token-under-test";
        var host = _HostWithConnections();
        Func<JsonElement, CancellationToken, Task<JsonElement>>? handler = null;
        host.Channel.Handle(DepotChannel.ConnectWithInstallToken, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(handle => handler = handle));
        IReadOnlyDictionary<string, string>? grant = null;
        host.SignInMcpServerWithGrantAsync("Depot: Work", "urn:depot:grant-type:install_token", Arg.Do<IReadOnlyDictionary<string, string>>(parameters => grant = parameters), Arg.Any<CancellationToken>())
            .Returns(outcome);
        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.NotNull(handler);
        var connect = handler;
        var answer = await connect(
            JsonSerializer.SerializeToElement(new DepotInstallTokenRequest(new DepotConnectionPayload("", "Work", "https://depot.example.com/mcp"), token), DepotChannel.Json),
            CancellationToken.None);

        Assert.Equal(message, answer.Deserialize<DepotInstallTokenAnswer>(DepotChannel.Json)?.Message);
        Assert.Equal(new Dictionary<string, string> { ["install_token"] = token, ["client_id"] = "depot" }, grant);
        Assert.Equal(connections, new Settings.DepotSettings(host.Storage).Connections.Count(connection => connection.Url == "https://depot.example.com"));
        Assert.False(answer.GetRawText().Contains(token, StringComparison.Ordinal), "The answer repeats the installation token.");
    }

}
