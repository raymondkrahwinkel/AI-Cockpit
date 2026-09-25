using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotPlugin`'s SaveConnections channel handler (AC-1394): the registry-sync side effects that used to run
// directly inside `Ui.DepotSettingsControl.Save` against `ICockpitHost` — syncing the memory-source and
// shared-project-source registries against the connections currently in storage, then reclaiming any orphaned
// MCP-registry entry — moved here with the plugin split. What used to be "the view's Save()" tests
// (DepotSettingsControlTests.cs, before AC-1394) live here now, driven directly at the handler DepotPlugin
// registers, the same pattern GitHubActionsPluginTests uses for its own backend/UI split. The view-only
// validation (a name/URL collision refuses the whole batch before ever reaching this handler) stays in
// DepotSettingsControlTests.cs, since it is genuinely UI-only and never runs here.
public class DepotPluginSaveConnectionsTests
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

    // Initializes a plugin against `host` and hands back the SaveConnections handler it registered — the same
    // Arg.Do capture GitHubActionsPluginTests uses for its own backend/UI split. Initialize's own startup
    // housekeeping (declaring the family, registering the seeded "before" list's own sources, reclaiming every
    // configured connection's old MCP entry) is not what these tests pin, so the call history is cleared right
    // after — only what invoking the handler itself does, below, is asserted on.
    private static Func<JsonElement, CancellationToken, Task<JsonElement>> _InitializeAndCaptureSaveHandler(ICockpitHost host)
    {
        var captured = new List<Func<JsonElement, CancellationToken, Task<JsonElement>>>();
        var channel = Substitute.For<IPluginBackendChannel>();
        channel.Handle(DepotChannel.SaveConnections, Arg.Do<Func<JsonElement, CancellationToken, Task<JsonElement>>>(h => captured.Add(h)))
            .Returns(Substitute.For<IDisposable>());
        host.Channel.Returns(channel);

        using var plugin = new DepotPlugin();
        plugin.Initialize(host);
        host.ClearReceivedCalls();

        return Assert.Single(captured);
    }

    // Invokes the captured handler with `after` as the saved connection list — the same request
    // Ui.DepotSettingsControl._Write builds.
    private static async Task _SaveAsync(Func<JsonElement, CancellationToken, Task<JsonElement>> handler, params DepotConnectionRegistration[] after)
    {
        var payload = JsonSerializer.SerializeToElement(
            new DepotSaveConnectionsRequest([.. after.Select(connection => new DepotConnectionPayload(connection.Id, connection.Name, connection.Url))]),
            DepotChannel.Json);

        await handler(payload, CancellationToken.None);
    }

    [Fact]
    public async Task Save_NewConnection_NeverPushesItIntoTheSharedMcpRegistry()
    {
        var host = _HostWithConnections();
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com"));

        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
    }

    [Fact]
    public async Task Save_RemovedConnection_ReclaimsItsOldMcpServerEntry()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler);

        _ = host.Received(1).RemoveMcpServer("Depot: Work");
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
    }

    // The guard this pins: a rename changes McpServerName, so the old entry would otherwise be left behind forever
    // — reclaimed here even though the handler no longer re-adds anything under the new name.
    [Fact]
    public async Task Save_RenamedConnection_ReclaimsTheOldNameAndAddsNothingNew()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Work (new)", "https://depot.example.com"));

        _ = host.Received(1).RemoveMcpServer("Depot: Work");
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
    }

    // AC-501: memory sources sync the same save a connection's MCP contribution does, live, without an app restart.
    [Fact]
    public async Task Save_NewConnection_RegistersItsOwnMemorySourceUnderThePlainDepotScheme()
    {
        var host = _HostWithConnections();
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));

        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Acme")));
    }

    [Fact]
    public async Task Save_SecondConnection_RegistersItUnderANamespacedScheme()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(
            handler,
            new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"));

        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration => registration.Scheme == "depot.wispslate"));
    }

    [Fact]
    public async Task Save_RemovedConnection_ReclaimsItsOldMemorySourceScheme()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler);

        host.Received(1).RemoveProjectMemorySource("depot");
        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
    }

    // The guard this pins: without this, a rename would only re-add under the same scheme (Register refuses it as
    // "already taken" — by itself) and the picker would keep showing the operator's old name forever.
    [Fact]
    public async Task Save_RenamedConnection_ReclaimsTheOldSchemeAndRegistersTheRenamedTitle()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme (renamed)", "https://depot.example.com"));

        host.Received(1).RemoveProjectMemorySource("depot");
        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Acme (renamed)")));
    }

    [Fact]
    public async Task Save_UnchangedConnection_DoesNotReRegisterItsMemorySource()
    {
        // Re-adding unchanged content would only hit Register's "scheme already taken" refusal — this pins that
        // the handler does not even try, rather than relying on the registry to swallow a no-op call quietly.
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));

        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
        host.DidNotReceive().RemoveProjectMemorySource(Arg.Any<string>());
    }

    // --- AC-245: shared-project sources sync the same save, the same live-refresh reasoning as memory sources ---

    [Fact]
    public async Task Save_NewConnection_RegistersItsOwnSharedProjectSourceUnderThePlainDepotKey()
    {
        var host = _HostWithConnections();
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));

        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source =>
            source.Key == "depot" && source.SourceName.Contains("Acme")));
    }

    [Fact]
    public async Task Save_SecondConnection_RegistersItsSharedProjectSourceUnderANamespacedKey()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(
            handler,
            new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"));

        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source => source.Key == "depot.wispslate"));
    }

    [Fact]
    public async Task Save_RemovedConnection_ReclaimsItsOldSharedProjectSourceKey()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler);

        host.Received(1).RemoveSharedProjectSource("depot");
        host.DidNotReceive().AddSharedProjectSource(Arg.Any<ISharedProjectSource>());
    }

    [Fact]
    public async Task Save_RenamedConnection_ReclaimsTheOldSharedProjectSourceKeyAndRegistersUnderTheSameKeyAgain()
    {
        // Unlike a memory source (whose Title changes on rename), the shared-project source's Key is the connection's
        // scheme, not its name — a rename keeps the same key, but the handler still reclaims and re-adds it because
        // the underlying DepotSharedProjectSource instance now closes over the renamed connection (for its SourceName).
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme (renamed)", "https://depot.example.com"));

        host.Received(1).RemoveSharedProjectSource("depot");
        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source =>
            source.Key == "depot" && source.SourceName.Contains("Acme (renamed)")));
    }

    [Fact]
    public async Task Save_UnchangedConnection_DoesNotReRegisterItsSharedProjectSource()
    {
        var host = _HostWithConnections(new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));

        host.DidNotReceive().AddSharedProjectSource(Arg.Any<ISharedProjectSource>());
        host.DidNotReceive().RemoveSharedProjectSource(Arg.Any<string>());
    }

    // AC-502/AC-503: `_SyncMemorySources` calls `BuildRegistrationPairs` twice, wiring new delegate closures
    // each time that are never delegate-equal. `ProjectMemorySourceRegistration`'s equality (AC-502)
    // deliberately ignores those closures, comparing only Scheme/Title/Instruction — so the plain `==` diff still skips an unchanged connection.
    [Fact]
    public async Task Save_UnchangedConnection_IsNotReRegistered_DespiteEachBuildRegistrationPairsCallWiringItsOwnClosures()
    {
        var connections = new[] { new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com") };
        var host = _HostWithConnections(connections);

        var first = DepotMemorySource.BuildRegistrationPairs(connections, host).Single().Registration;
        var second = DepotMemorySource.BuildRegistrationPairs(connections, host).Single().Registration;
        Assert.Equal(first, second);
        Assert.NotSame(first.CheckReachability, second.CheckReachability);
        Assert.NotSame(first.ListLocationsAsync, second.ListLocationsAsync);

        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, connections[0]);

        // And the handler — which runs exactly this shape internally — must not treat the connection as changed.
        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
        host.DidNotReceive().RemoveProjectMemorySource(Arg.Any<string>());
    }

    // A connection removed ahead of another in the list promotes the survivor into the primary slot — its scheme
    // changes from a namespaced one to the plain "depot", which existing "depot:<slug>"-linked projects rely on.
    [Fact]
    public async Task Save_RemovingThePrimaryConnection_PromotesTheSurvivorToTheDepotScheme()
    {
        var host = _HostWithConnections(
            new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"));
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"));

        host.Received(1).RemoveProjectMemorySource("depot");
        host.Received(1).RemoveProjectMemorySource("depot.wispslate");
        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Wispslate")));
    }

    private static FakeMemorySourceRegistry _WireRegistry(ICockpitHost host)
    {
        var registry = new FakeMemorySourceRegistry();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registry.Add(call.Arg<ProjectMemorySourceRegistration>()));
        host.When(cockpit => cockpit.RemoveProjectMemorySource(Arg.Any<string>()))
            .Do(call => registry.Remove(call.Arg<string>()));
        return registry;
    }

    // Regression: retiring/registering per connection one at a time let an Add claim a scheme a later
    // connection in the same save still held, silently dropping one on a name swap. A call-counting
    // substitute can't see that ordering gap, so this asserts on a registry stand-in's end state instead.
    [Fact]
    public async Task Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry()
    {
        var connections = new[]
        {
            new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
            new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
            new DepotConnectionRegistration("conn-3", "Gamma", "https://gamma.example.com"),
        };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(
            handler,
            connections[0],
            connections[1] with { Name = "Gamma" },
            connections[2] with { Name = "Beta" });

        Assert.Equal(3, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta) && beta.Title.Contains("Beta"));
        Assert.True(registry.Sources.TryGetValue("depot.gamma", out var gamma) && gamma.Title.Contains("Gamma"));
    }

    // AC-499: Equals now also compares FamilyKey/InstanceTitle, which _SyncMemorySources' before/after diff
    // relies on. These pin the registry's actual end state, not call counts, for the same ordering-bug
    // reasoning as Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry above.

    [Fact]
    public async Task Save_NewConnection_EndState_CarriesTheDepotFamilyKeyAndItsOwnInstanceTitle()
    {
        var host = _HostWithConnections();
        var registry = _WireRegistry(host);
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"));

        Assert.True(registry.Sources.TryGetValue("depot", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("Acme", registration.InstanceTitle);
    }

    // Folds the old DepotSettingsControlTests.SignInAsync_RenamedRow_EndState_MemorySourceInstanceTitleFollowsTheRename
    // (AC-499) into this one: Save and a row's Sign-in click both funnel through this same channel call now, so
    // there is no longer a separate code path for "saved via Sign-in" to pin apart from "saved via the Save button".
    [Fact]
    public async Task Save_RenamedConnection_EndState_InstanceTitleFollowsTheRename()
    {
        var connections = new[] { new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com") };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, connections[0] with { Name = "Acme (renamed)" });

        Assert.True(registry.Sources.TryGetValue("depot", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("Acme (renamed)", registration.InstanceTitle);
    }

    [Fact]
    public async Task Save_SwappingTwoConnectionNames_EndState_InstanceTitlesFollowTheSwap()
    {
        // Same swap shape as Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry above, checked
        // against InstanceTitle specifically: a name swap must not leave either row's instance dropdown label
        // pointing at the other connection's name.
        var connections = new[]
        {
            new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
            new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
            new DepotConnectionRegistration("conn-3", "Gamma", "https://gamma.example.com"),
        };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(
            handler,
            connections[0],
            connections[1] with { Name = "Gamma" },
            connections[2] with { Name = "Beta" });

        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta));
        Assert.Equal("Beta", beta!.InstanceTitle);
        Assert.True(registry.Sources.TryGetValue("depot.gamma", out var gamma));
        Assert.Equal("Gamma", gamma!.InstanceTitle);
        Assert.All(registry.Sources.Values, registration => Assert.Equal("depot", registration.FamilyKey));
    }

    [Fact]
    public async Task Save_RenameProducesASymbolOnlyName_EndState_FallsBackToTheIdSchemeButKeepsTheRealInstanceTitle()
    {
        // The name-slug fallback in DepotMemorySource._NamespacedScheme kicks in for the scheme, but the
        // InstanceTitle shown in the picker must still read the operator's literal (symbol-only) name, not the
        // scheme's id-based fallback.
        var connections = new[]
        {
            new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
            new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"),
        };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(handler, connections[0], connections[1] with { Name = "★★★" });

        Assert.True(registry.Sources.TryGetValue("depot.conn-2", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("★★★", registration.InstanceTitle);
    }

    [Fact]
    public async Task Save_TwoNonPrimaryConnectionsSlugCollide_EndState_BothSurviveUnderDistinctSchemesWithTheirOwnInstanceTitles()
    {
        // Two connections named alike enough to slugify to the same string ("Work"/"work!") — DepotMemorySource
        // falls the second back to its own connection id, so both still end up in the registry rather than one
        // silently losing the "scheme already taken" race.
        var connections = new[] { new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com") };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        await _SaveAsync(
            handler,
            connections[0],
            new DepotConnectionRegistration("conn-2", "Work", "https://work-a.example.com"),
            new DepotConnectionRegistration("conn-3", "work!", "https://work-b.example.com"));

        Assert.Equal(3, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.work", out var first));
        Assert.Equal("Work", first!.InstanceTitle);
        var second = registry.Sources.Values.Single(registration => registration != first && registration.Scheme != "depot");
        Assert.Equal("work!", second.InstanceTitle);
        Assert.Equal("depot", second.FamilyKey);
    }

    // Renamed from the old DepotSettingsControlTests.SignInAsync_TwoConsecutiveSaves_RenameThenRenameBack_...: a
    // row's Sign-in click and the Save button funnel through the same channel call now, so this pins the handler's
    // own before/after diff across two saves in a row rather than a UI sign-in flow. Storage is updated between the
    // two calls the same way the real UI's own Ui.DepotSettingsControl._Write updates it before every channel call.
    [Fact]
    public async Task Save_TwoConsecutiveSaves_RenameThenRenameBack_EndStateHasOnlyTheCurrentScheme()
    {
        var connections = new[]
        {
            new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
            new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
        };
        var host = _HostWithConnections(connections);
        var registry = _WireRegistry(host);
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(connections, host))
        {
            registry.Add(pair.Registration);
        }
        var handler = _InitializeAndCaptureSaveHandler(host);

        var renamed = connections[1] with { Name = "Gamma" };
        await _SaveAsync(handler, connections[0], renamed);
        new Settings.DepotSettings(host.Storage) { Connections = [connections[0], renamed] };

        await _SaveAsync(handler, connections[0], connections[1]);

        Assert.Equal(2, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta));
        Assert.Equal("Beta", beta!.InstanceTitle);
        Assert.False(registry.Sources.ContainsKey("depot.gamma"));
    }

    // A minimal stand-in for `ProjectMemorySourceRegistry` (internal to Cockpit.App) with the same
    // first-one-wins-by-scheme rule. Exists because a call-counting substitute cannot see an ordering bug
    // where a later Add silently loses to an earlier connection's not-yet-retired scheme.
    private sealed class FakeMemorySourceRegistry
    {
        private readonly Dictionary<string, ProjectMemorySourceRegistration> _sources = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ProjectMemorySourceRegistration> Sources => _sources;

        public void Add(ProjectMemorySourceRegistration registration) => _sources.TryAdd(registration.Scheme, registration);

        public void Remove(string scheme) => _sources.Remove(scheme);
    }
}
