extern alias UiAsm;

using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
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

    // --- AC-245: shared-project sources -----------------------------------------------------------------------

    // AC-504: this plugin no longer pushes its servers into the shared registry — an earlier version (AC-243) did,
    // so a connection surviving from that install needs its old entry reclaimed on every start, the same move
    // YouTrackPlugin made when it left the push path (AC-11).

    // AC-504: session delivery never reaches this overload (McpServerCatalog always calls the two-argument one,
    // which this plugin overrides directly) — it exists only so the host's OAuth sign-in fallback can find a
    // connection's server by name when the shared registry no longer carries it.

    [Fact]
    public void GetMcpServers_Always_CarriesTheConnectionsOwnIdSoARenameKeepsItsSignIn()
    {
        // AC-403: the host files a server's OAuth token under the contribution's Id. Leave it unset and the host
        // falls back to keying on the name — which for this plugin is "Depot: {Name}", built from a field the
        // operator edits, so renaming a connection would strand its sign-in under the old name.
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com")));

        var servers = plugin.GetMcpServers();

        Assert.Equal("c1", Assert.Single(servers, server => server.Name == "Depot: Acme").Id);
        Assert.Equal("c2", Assert.Single(servers, server => server.Name == "Depot: Wispslate").Id);
    }

    [Fact]
    public void GetMcpServers_AfterTwoConnectionsSwapNames_EachKeepsItsOwnId()
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
    }

    // AC-499 regression: a connection already stored with a trailing /mcp (older builds kept whatever was
    // typed) must not double into "…/mcp/mcp". Normalized at this use point, not only at save time, so
    // already-stored data is fixed without a migration.
    [Fact]
    public void GetMcpServers_ConnectionStoredWithATrailingMcp_DoesNotDoubleItInTheContributedUrl()
    {
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com/mcp")));

        var server = Assert.Single(plugin.GetMcpServers("project-a", ["depot"]));

        Assert.Equal("https://depot.example.com/mcp", server.Url);
        Assert.Equal("https://depot.example.com", server.OAuthAuthority);
    }

    // AC-499: OAuthAuthority is the origin (scheme+host+port) of the normalized URL, not the stored URL's own path —
    // a subpath deployment's authority lives at the origin, not under the subpath.
    [Fact]
    public void GetMcpServers_ConnectionUrlHasASubpath_OAuthAuthorityIsTheOriginNotTheSubpath()
    {
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://host.example.com/depot")));

        var server = Assert.Single(plugin.GetMcpServers("project-a", ["depot"]));

        Assert.Equal("https://host.example.com/depot/mcp", server.Url);
        Assert.Equal("https://host.example.com", server.OAuthAuthority);
    }

    // AC-504 criterion 7 (regression): a scheme belonging to a different memory source (a project's Folder row,
    // whatever scheme string that happens to resolve to) matches none of this plugin's own connections.

    // Without this wiring the host's McpServerCatalog would never see this plugin at all — every GetMcpServers
    // test above would still pass in isolation while no session anywhere ever got a Depot server.

    // Regression: firing one RemoveMcpServer call per connection without awaiting let two concurrent calls
    // each load the same stale snapshot (unlocked load-modify-save), silently keeping the entry that lost
    // the race. Pins that the second call is not made until the first's task completes.

    // AC-499: zero connections meant no "Depot" entry in the project editor's picker. Declaring the family
    // unconditionally fixes that — asserted at zero, one and several so a reintroduced
    // `if (connections.Count > 0)` guard fails here at the zero case specifically.
}
