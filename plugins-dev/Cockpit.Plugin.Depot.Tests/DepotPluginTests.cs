using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
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
    public void Initialize_NoConnectionsConfigured_RegistersNoMemorySource()
    {
        // Acceptance criterion 5: the row behaves exactly as it did before this plugin existed when nothing is
        // configured, rather than always offering a fixed "Depot project" nothing points at.
        var host = _HostWithConnections();

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
    }

    [Fact]
    public void Initialize_OneConnectionConfigured_RegistersItUnderThePlainDepotScheme()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"));
        var registered = new List<ProjectMemorySourceRegistration>();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registered.Add(call.Arg<ProjectMemorySourceRegistration>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        var registration = Assert.Single(registered);
        Assert.Equal("depot", registration.Scheme);
        Assert.Contains("Acme", registration.Title);
        Assert.Contains("Depot MCP", registration.Instruction);
        Assert.Contains("say so rather than working from memory you cannot see", registration.Instruction);
    }

    [Fact]
    public void Initialize_TwoConnectionsConfigured_RegistersBothUnderDistinctSchemes()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));
        var registered = new List<ProjectMemorySourceRegistration>();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registered.Add(call.Arg<ProjectMemorySourceRegistration>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.Equal(2, registered.Count);
        Assert.Equal("depot", registered[0].Scheme);
        Assert.Equal("depot.wispslate", registered[1].Scheme);
    }

    // --- AC-245: shared-project sources -----------------------------------------------------------------------

    [Fact]
    public void Initialize_NoConnectionsConfigured_RegistersNoSharedProjectSource()
    {
        var host = _HostWithConnections();

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.DidNotReceive().AddSharedProjectSource(Arg.Any<ISharedProjectSource>());
    }

    [Fact]
    public void Initialize_OneConnectionConfigured_RegistersOneSharedProjectSourceUnderThePlainDepotScheme()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"));
        var registered = new List<ISharedProjectSource>();
        host.When(cockpit => cockpit.AddSharedProjectSource(Arg.Any<ISharedProjectSource>()))
            .Do(call => registered.Add(call.Arg<ISharedProjectSource>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        var source = Assert.Single(registered);
        Assert.Equal("depot", source.Key); // same scheme AddProjectMemorySource registered above — Id/MemoryRef line up
        Assert.Contains("Acme", source.SourceName);
    }

    [Fact]
    public void Initialize_TwoConnectionsConfigured_RegistersTwoSharedProjectSourcesUnderDistinctKeys()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));
        var registered = new List<ISharedProjectSource>();
        host.When(cockpit => cockpit.AddSharedProjectSource(Arg.Any<ISharedProjectSource>()))
            .Do(call => registered.Add(call.Arg<ISharedProjectSource>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.Equal(2, registered.Count);
        Assert.Equal("depot", registered[0].Key);
        Assert.Equal("depot.wispslate", registered[1].Key);
    }

    [Fact]
    public void Metadata_Always_MatchesTheManifestTheHostLoadsBy()
    {
        Assert.Equal("depot", new DepotPlugin().Metadata.Id);
    }

    // AC-504: this plugin no longer pushes its servers into the shared registry — an earlier version (AC-243) did,
    // so a connection surviving from that install needs its old entry reclaimed on every start, the same move
    // YouTrackPlugin made when it left the push path (AC-11).
    [Fact]
    public void Initialize_ConnectionsConfigured_ReclaimsEachConnectionsOldMcpRegistryEntry()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        _ = host.Received(1).RemoveMcpServer("Depot: Acme");
        _ = host.Received(1).RemoveMcpServer("Depot: Wispslate");
    }

    [Fact]
    public void Initialize_NoConnectionsConfigured_ReclaimsNothing()
    {
        var host = _HostWithConnections();

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        _ = host.DidNotReceive().RemoveMcpServer(Arg.Any<string>());
    }

    // AC-504: session delivery never reaches this overload (McpServerCatalog always calls the two-argument one,
    // which this plugin overrides directly) — it exists only so the host's OAuth sign-in fallback can find a
    // connection's server by name when the shared registry no longer carries it.
    [Fact]
    public void GetMcpServers_NoArgOverload_ReturnsEveryConfiguredConnectionUnscoped()
    {
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com")));

        var servers = plugin.GetMcpServers();

        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, server => server.Name == "Depot: Acme");
        Assert.Contains(servers, server => server.Name == "Depot: Wispslate");
    }

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
        // Acceptance criterion 3, the Depot half. Two connections on the same host swap names: they stay unique, so
        // nothing refuses the save, and the derived MCP server names swap with them. If identity followed the name,
        // each would inherit the other's token — and since McpOAuthToken.IsForResource only bounds a token to
        // scheme/host/port, two instances on one host with different paths would pass that check too, and one
        // connection would present the other's bearer to an endpoint it was never issued for.
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

    [Fact]
    public void GetMcpServers_NoArgOverload_BeforeInitialize_ReturnsEmpty()
    {
        using var plugin = new DepotPlugin();

        Assert.Empty(plugin.GetMcpServers());
    }

    [Fact]
    public void GetMcpServers_BeforeInitialize_ReturnsEmpty()
    {
        using var plugin = new DepotPlugin();

        Assert.Empty(plugin.GetMcpServers("project-a", ["depot"]));
    }

    [Fact]
    public void GetMcpServers_NoMemorySchemes_ReturnsNothing()
    {
        // AC-504 criterion 2: a project without a Depot memory row gets no Depot server at all.
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com")));

        Assert.Empty(plugin.GetMcpServers("project-a", []));
    }

    [Fact]
    public void GetMcpServers_SchemeMatchesOneConnection_ReturnsOnlyThatConnectionsServer()
    {
        // AC-504 criterion 1: the connection the project's own memory row points at, not another configured one.
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com")));

        var servers = plugin.GetMcpServers("project-a", ["depot.wispslate"]);

        var server = Assert.Single(servers);
        Assert.Equal("Depot: Wispslate", server.Name);
        Assert.Equal("https://wispslate.example.com/mcp", server.Url);
        Assert.Equal("https://wispslate.example.com", server.OAuthAuthority);
    }

    // AC-499 regression, the measured defect: a connection already stored with a trailing /mcp of its own (Depot's
    // own docs tell the operator to paste the full endpoint, and older builds — or storage from before this fix —
    // kept whatever was typed) must not double into "…/mcp/mcp" every time a session asks this plugin for its
    // servers. Normalized at this use point, not only at save time, so already-stored data is fixed without a
    // migration.
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

    [Fact]
    public void GetMcpServers_TwoMemoryRowsForTwoConnections_ReturnsBoth()
    {
        // AC-504 criterion 2: a project with two Memory rows pointing at two Depot connections gets both.
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com")));

        var servers = plugin.GetMcpServers("project-a", ["depot", "depot.wispslate"]);

        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, server => server.Name == "Depot: Acme");
        Assert.Contains(servers, server => server.Name == "Depot: Wispslate");
    }

    // AC-504 criterion 7 (regression): a scheme belonging to a different memory source (a project's Folder row,
    // whatever scheme string that happens to resolve to) matches none of this plugin's own connections.
    [Fact]
    public void GetMcpServers_SchemeBelongsToADifferentSource_ReturnsNothing()
    {
        using var plugin = new DepotPlugin();
        plugin.Initialize(_HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com")));

        Assert.Empty(plugin.GetMcpServers("project-a", ["folder"]));
    }

    // Without this wiring the host's McpServerCatalog would never see this plugin at all — every GetMcpServers
    // test above would still pass in isolation while no session anywhere ever got a Depot server.
    [Fact]
    public void ConfigureServices_Always_RegistersItselfAsTheSameInstanceThatWasInitialized()
    {
        using var plugin = new DepotPlugin();
        var services = new ServiceCollection();

        plugin.ConfigureServices(services);

        var descriptor = Assert.Single(services, service => service.ServiceType == typeof(IPluginMcpProvider));
        Assert.Same(plugin, descriptor.ImplementationInstance);
    }

    // Regression: the earlier version of this reclaim fired one RemoveMcpServer call per connection without
    // awaiting between them. RemoveMcpServer does its own unlocked load-modify-save round trip against the shared
    // store, so two concurrent calls for two different connections can each load the same stale snapshot and the
    // last SaveAsync to finish silently keeps whichever connection lost the race — exactly the stale registry entry
    // this reclaim exists to remove. This pins that the second call is not even made until the first's task
    // completes.
    [Fact]
    public async Task Initialize_TwoConnectionsConfigured_ReclaimsThemSequentially_NotConcurrently()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));
        var firstRemoval = new TaskCompletionSource();
        host.RemoveMcpServer("Depot: Acme").Returns(_ => firstRemoval.Task);

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        _ = host.Received(1).RemoveMcpServer("Depot: Acme");
        _ = host.DidNotReceive().RemoveMcpServer("Depot: Wispslate");

        firstRemoval.SetResult();
        await _WaitUntilAsync(() => host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.RemoveMcpServer)) == 2);

        _ = host.Received(1).RemoveMcpServer("Depot: Wispslate");
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // --- AC-499: the family is declared unconditionally --------------------------------------------------------
    // The bug this ticket exists to close: zero connections meant no "Depot" entry anywhere in the project editor's
    // picker and no way to reach this plugin's settings from it. Declaring the family regardless of how many
    // connections are configured is what fixes that — asserted at zero, one and several so a future regression that
    // reintroduces an `if (connections.Count > 0)` guard fails here at the zero case specifically.

    [Fact]
    public void Initialize_NoConnectionsConfigured_StillDeclaresTheDepotFamily()
    {
        var host = _HostWithConnections();

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.Received(1).AddProjectMemorySourceFamily(Arg.Is<ProjectMemorySourceFamily>(family =>
            family.Key == "depot" && family.Title == "Depot"));
    }

    [Fact]
    public void Initialize_OneConnectionConfigured_DeclaresTheDepotFamilyExactlyOnce()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.Received(1).AddProjectMemorySourceFamily(Arg.Any<ProjectMemorySourceFamily>());
    }

    [Fact]
    public void Initialize_SeveralConnectionsConfigured_DeclaresTheDepotFamilyExactlyOnce()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("c1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("c2", "Wispslate", "https://wispslate.example.com"));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        host.Received(1).AddProjectMemorySourceFamily(Arg.Any<ProjectMemorySourceFamily>());
    }

    [Fact]
    public void Initialize_Always_SetsTheEmptyHintNamingDepot()
    {
        var host = _HostWithConnections();
        var declared = new List<ProjectMemorySourceFamily>();
        host.When(cockpit => cockpit.AddProjectMemorySourceFamily(Arg.Any<ProjectMemorySourceFamily>()))
            .Do(call => declared.Add(call.Arg<ProjectMemorySourceFamily>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);

        Assert.Equal("No Depot server configured yet.", Assert.Single(declared).EmptyHint);
    }

    [Fact]
    public async Task Family_ConfigureAsync_OpensThisPluginsOwnSettings()
    {
        var host = _HostWithConnections();
        var declared = new List<ProjectMemorySourceFamily>();
        host.When(cockpit => cockpit.AddProjectMemorySourceFamily(Arg.Any<ProjectMemorySourceFamily>()))
            .Do(call => declared.Add(call.Arg<ProjectMemorySourceFamily>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);
        var family = Assert.Single(declared);

        Assert.NotNull(family.ConfigureAsync);
        await family.ConfigureAsync!(CancellationToken.None);

        await host.Received(1).ShowSettingsAsync();
    }

    [Fact]
    public async Task Family_ConfigureAsync_CalledTwice_CallsShowSettingsAsyncExactlyTwice()
    {
        // Pins that ConfigureAsync does not somehow call ShowSettingsAsync more than once per invocation (e.g. a
        // fire-and-forget left over from a refactor) — one click, one open.
        var host = _HostWithConnections();
        var declared = new List<ProjectMemorySourceFamily>();
        host.When(cockpit => cockpit.AddProjectMemorySourceFamily(Arg.Any<ProjectMemorySourceFamily>()))
            .Do(call => declared.Add(call.Arg<ProjectMemorySourceFamily>()));

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);
        var configureAsync = Assert.Single(declared).ConfigureAsync!;

        await configureAsync(CancellationToken.None);
        await configureAsync(CancellationToken.None);

        await host.Received(2).ShowSettingsAsync();
    }
}
