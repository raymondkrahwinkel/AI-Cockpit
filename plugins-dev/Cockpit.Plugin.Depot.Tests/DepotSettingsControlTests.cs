using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotSettingsControl.Save"/> (AC-243, reworked AC-504): persists the connection list, and reclaims a
/// removed or renamed connection's <em>old</em> "Depot: &lt;name&gt;" MCP-registry entry — left over from an install
/// that predates AC-504's move to offering a connection's server per-project instead of pushing it into the shared
/// registry (the orphan-cleanup KubernetesSettingsControl.Save does for a cluster's secret, applied to the registry
/// instead). Save never adds to that registry any more: a kept or new connection's server is what
/// <see cref="DepotPlugin.GetMcpServers(string?, IReadOnlyList{string})"/> answers with, not a registry row.
/// </summary>
[Collection("avalonia")]
public class DepotSettingsControlTests
{
    [Fact]
    public void Save_NewConnection_NeverPushesItIntoTheSharedMcpRegistry()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work", url: "https://depot.example.com");

        var saved = view.Save();

        Assert.True(saved);
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
        Assert.Equal("Work", settings.Connections.Single().Name);
    }

    [Fact]
    public void Save_RemovedConnection_ReclaimsItsOldMcpServerEntry()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _RemoveRow(view, index: 0);

        var saved = view.Save();

        Assert.True(saved);
        _ = host.Received(1).RemoveMcpServer("Depot: Work");
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
        Assert.Empty(settings.Connections);
    }

    // The guard this pins: a rename changes McpServerName, so the old entry would otherwise be left behind forever
    // — reclaimed here even though Save no longer re-adds anything under the new name.
    [Fact]
    public void Save_RenamedConnection_ReclaimsTheOldNameAndAddsNothingNew()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work (new)", url: "https://depot.example.com");

        view.Save();

        _ = host.Received(1).RemoveMcpServer("Depot: Work");
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
    }

    // The guard this pins: two rows saved under the same name would leave the registry unable to tell them apart —
    // Save() refuses the whole batch rather than keep one and drop the other (mirrors McpServersViewModel.Save's own
    // duplicate-name refusal in the host dialog), so a same-named pair of brand-new rows leaves storage exactly as
    // empty as it started, not holding whichever row happened to sort first.
    [Fact]
    public void Save_TwoRowsWithTheSameName_RefusesTheWholeSave_AndWritesNothing()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: "Work", url: "https://first.example.com");
        _SetRowFields(view, index: 1, name: "Work", url: "https://second.example.com");

        var saved = view.Save();

        Assert.False(saved);
        Assert.Empty(settings.Connections);
    }

    // Case-insensitive on purpose (Ordinal → OrdinalIgnoreCase): ProjectMemorySourceRegistration.Register (the
    // memory-source registry a save also writes to) refuses a colliding scheme case-insensitively, so "Work"/"work"
    // would already collide one layer down even though Depot's own McpServerName comparison used to let them both
    // through as two distinct "Depot: Work"/"Depot: work" entries.
    [Fact]
    public void Save_TwoRowsWithNamesDifferingOnlyByCase_RefusesTheWholeSave_AndWritesNothing()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: "Work", url: "https://first.example.com");
        _SetRowFields(view, index: 1, name: "work", url: "https://second.example.com");

        var saved = view.Save();

        Assert.False(saved);
        Assert.Empty(settings.Connections);
    }

    // AC-501: memory sources sync the same save a connection's MCP contribution does, live, without an app restart.
    [Fact]
    public void Save_NewConnection_RegistersItsOwnMemorySourceUnderThePlainDepotScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme", url: "https://depot.example.com");

        view.Save();

        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Acme")));
    }

    [Fact]
    public void Save_SecondConnection_RegistersItUnderANamespacedScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 1, name: "Wispslate", url: "https://wispslate.example.com");

        view.Save();

        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration => registration.Scheme == "depot.wispslate"));
    }

    [Fact]
    public void Save_RemovedConnection_ReclaimsItsOldMemorySourceScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _RemoveRow(view, index: 0);

        view.Save();

        host.Received(1).RemoveProjectMemorySource("depot");
        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
    }

    // The guard this pins: without this, a rename would only re-add under the same scheme (Register refuses it as
    // "already taken" — by itself) and the picker would keep showing the operator's old name forever.
    [Fact]
    public void Save_RenamedConnection_ReclaimsTheOldSchemeAndRegistersTheRenamedTitle()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme (renamed)", url: "https://depot.example.com");

        view.Save();

        host.Received(1).RemoveProjectMemorySource("depot");
        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Acme (renamed)")));
    }

    [Fact]
    public void Save_UnchangedConnection_DoesNotReRegisterItsMemorySource()
    {
        // Re-adding unchanged content would only hit Register's "scheme already taken" refusal — this pins that
        // Save() does not even try, rather than relying on the registry to swallow a no-op call quietly.
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);

        view.Save();

        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
        host.DidNotReceive().RemoveProjectMemorySource(Arg.Any<string>());
    }

    // --- AC-245: shared-project sources sync the same save, the same live-refresh reasoning as memory sources ---

    [Fact]
    public void Save_NewConnection_RegistersItsOwnSharedProjectSourceUnderThePlainDepotKey()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme", url: "https://depot.example.com");

        view.Save();

        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source =>
            source.Key == "depot" && source.SourceName.Contains("Acme")));
    }

    [Fact]
    public void Save_SecondConnection_RegistersItsSharedProjectSourceUnderANamespacedKey()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 1, name: "Wispslate", url: "https://wispslate.example.com");

        view.Save();

        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source => source.Key == "depot.wispslate"));
    }

    [Fact]
    public void Save_RemovedConnection_ReclaimsItsOldSharedProjectSourceKey()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _RemoveRow(view, index: 0);

        view.Save();

        host.Received(1).RemoveSharedProjectSource("depot");
        host.DidNotReceive().AddSharedProjectSource(Arg.Any<ISharedProjectSource>());
    }

    [Fact]
    public void Save_RenamedConnection_ReclaimsTheOldSharedProjectSourceKeyAndRegistersUnderTheSameKeyAgain()
    {
        // Unlike a memory source (whose Title changes on rename), the shared-project source's Key is the connection's
        // scheme, not its name — a rename keeps the same key, but Save still reclaims and re-adds it because the
        // underlying DepotSharedProjectSource instance now closes over the renamed connection (for its SourceName).
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme (renamed)", url: "https://depot.example.com");

        view.Save();

        host.Received(1).RemoveSharedProjectSource("depot");
        host.Received(1).AddSharedProjectSource(Arg.Is<ISharedProjectSource>(source =>
            source.Key == "depot" && source.SourceName.Contains("Acme (renamed)")));
    }

    [Fact]
    public void Save_UnchangedConnection_DoesNotReRegisterItsSharedProjectSource()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);

        view.Save();

        host.DidNotReceive().AddSharedProjectSource(Arg.Any<ISharedProjectSource>());
        host.DidNotReceive().RemoveSharedProjectSource(Arg.Any<string>());
    }

    /// <summary>
    /// AC-502/AC-503, explicitly: <see cref="DepotSettingsControl._SyncMemorySources"/> (private) calls
    /// <see cref="DepotMemorySource.BuildRegistrationPairs"/> twice for the same connection content — once for
    /// <c>_originalConnections</c>, once for the freshly-saved list — and each call wires brand-new
    /// <see cref="ProjectMemorySourceRegistration.ListLocationsAsync"/>/<see cref="ProjectMemorySourceRegistration.SignInAsync"/>/
    /// <see cref="ProjectMemorySourceRegistration.CheckReachability"/> closures over that call's own connection
    /// instance. Two such closures are never delegate-equal, but <see cref="ProjectMemorySourceRegistration"/>'s own
    /// equality override (AC-502) deliberately ignores all three, comparing only Scheme/Title/Instruction — so the
    /// record's own <c>==</c> correctly reads two independently-built registrations for the same connection as
    /// equal, which is exactly what lets <see cref="DepotSettingsControl._SyncMemorySources"/>'s plain <c>==</c>
    /// diff (no hand-rolled comparison needed) skip an unchanged connection.
    /// </summary>
    [Fact]
    public void Save_UnchangedConnection_IsNotReRegistered_DespiteEachBuildRegistrationPairsCallWiringItsOwnClosures()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };

        var first = DepotMemorySource.BuildRegistrationPairs(settings.Connections, host).Single().Registration;
        var second = DepotMemorySource.BuildRegistrationPairs(settings.Connections, host).Single().Registration;
        Assert.Equal(first, second);
        Assert.NotSame(first.CheckReachability, second.CheckReachability);
        Assert.NotSame(first.ListLocationsAsync, second.ListLocationsAsync);

        var view = new DepotSettingsControl(host, settings);

        view.Save();

        // And Save — which runs exactly this shape internally — must not treat the connection as changed.
        host.DidNotReceive().AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>());
        host.DidNotReceive().RemoveProjectMemorySource(Arg.Any<string>());
    }

    // A connection removed ahead of another in the list promotes the survivor into the primary slot — its scheme
    // changes from a namespaced one to the plain "depot", which existing "depot:<slug>"-linked projects rely on.
    [Fact]
    public void Save_RemovingThePrimaryConnection_PromotesTheSurvivorToTheDepotScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
                new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"),
            ],
        };
        var view = new DepotSettingsControl(host, settings);
        _RemoveRow(view, index: 0);

        view.Save();

        host.Received(1).RemoveProjectMemorySource("depot");
        host.Received(1).RemoveProjectMemorySource("depot.wispslate");
        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Wispslate")));
    }

    // Regression: retiring a stale scheme and registering the new one per connection, one connection at a time,
    // let an Add claim a scheme a later connection in the same save still held under its own before-registration —
    // two connections swapping names silently dropped one of the two from the registry until a restart. A
    // call-counting substitute cannot see that gap (both Adds and both Removes still happen, just in an order that
    // loses one), so this wires the host's calls into a small registry stand-in and asserts on its end state.
    [Fact]
    public void Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry()
    {
        var registry = new FakeMemorySourceRegistry();
        var host = Substitute.For<ICockpitHost>();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registry.Add(call.Arg<ProjectMemorySourceRegistration>()));
        host.When(cockpit => cockpit.RemoveProjectMemorySource(Arg.Any<string>()))
            .Do(call => registry.Remove(call.Arg<string>()));

        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
                new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
                new DepotConnectionRegistration("conn-3", "Gamma", "https://gamma.example.com"),
            ],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }

        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "Gamma", url: "https://beta.example.com");
        _SetRowFields(view, index: 2, name: "Beta", url: "https://gamma.example.com");

        view.Save();

        Assert.Equal(3, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta) && beta.Title.Contains("Beta"));
        Assert.True(registry.Sources.TryGetValue("depot.gamma", out var gamma) && gamma.Title.Contains("Gamma"));
    }

    // --- AC-499: _SyncMemorySources end-state under the widened equality (FamilyKey/InstanceTitle now included) --
    // ProjectMemorySourceRegistration.Equals now also compares FamilyKey/InstanceTitle (AC-499), which
    // _SyncMemorySources' before/after diff relies on. These pin the registry's actual end state — not call counts,
    // which cannot see an ordering bug where a later Add silently loses to an earlier connection's own
    // not-yet-retired scheme (see Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry above, the
    // same reasoning applied to FamilyKey/InstanceTitle specifically).

    private static FakeMemorySourceRegistry _WireRegistry(ICockpitHost host)
    {
        var registry = new FakeMemorySourceRegistry();
        host.When(cockpit => cockpit.AddProjectMemorySource(Arg.Any<ProjectMemorySourceRegistration>()))
            .Do(call => registry.Add(call.Arg<ProjectMemorySourceRegistration>()));
        host.When(cockpit => cockpit.RemoveProjectMemorySource(Arg.Any<string>()))
            .Do(call => registry.Remove(call.Arg<string>()));
        return registry;
    }

    [Fact]
    public void Save_NewConnection_EndState_CarriesTheDepotFamilyKeyAndItsOwnInstanceTitle()
    {
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme", url: "https://depot.example.com");

        view.Save();

        Assert.True(registry.Sources.TryGetValue("depot", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("Acme", registration.InstanceTitle);
    }

    [Fact]
    public void Save_RenamedConnection_EndState_InstanceTitleFollowsTheRename()
    {
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme (renamed)", url: "https://depot.example.com");

        view.Save();

        Assert.True(registry.Sources.TryGetValue("depot", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("Acme (renamed)", registration.InstanceTitle);
    }

    [Fact]
    public void Save_SwappingTwoConnectionNames_EndState_InstanceTitlesFollowTheSwap()
    {
        // Same swap shape as Save_SwappingTwoConnectionNames_BothMemorySourcesSurviveInTheRegistry above, checked
        // against InstanceTitle specifically: a name swap must not leave either row's instance dropdown label
        // pointing at the other connection's name.
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
                new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
                new DepotConnectionRegistration("conn-3", "Gamma", "https://gamma.example.com"),
            ],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "Gamma", url: "https://beta.example.com");
        _SetRowFields(view, index: 2, name: "Beta", url: "https://gamma.example.com");

        view.Save();

        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta));
        Assert.Equal("Beta", beta!.InstanceTitle);
        Assert.True(registry.Sources.TryGetValue("depot.gamma", out var gamma));
        Assert.Equal("Gamma", gamma!.InstanceTitle);
        Assert.All(registry.Sources.Values, registration => Assert.Equal("depot", registration.FamilyKey));
    }

    [Fact]
    public void Save_RenameProducesASymbolOnlyName_EndState_FallsBackToTheIdSchemeButKeepsTheRealInstanceTitle()
    {
        // The name-slug fallback in DepotMemorySource._NamespacedScheme kicks in for the scheme, but the
        // InstanceTitle shown in the picker must still read the operator's literal (symbol-only) name, not the
        // scheme's id-based fallback.
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com"),
                new DepotConnectionRegistration("conn-2", "Wispslate", "https://wispslate.example.com"),
            ],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "★★★", url: "https://wispslate.example.com");

        view.Save();

        Assert.True(registry.Sources.TryGetValue("depot.conn-2", out var registration));
        Assert.Equal("depot", registration!.FamilyKey);
        Assert.Equal("★★★", registration.InstanceTitle);
    }

    [Fact]
    public void Save_TwoNonPrimaryConnectionsSlugCollide_EndState_BothSurviveUnderDistinctSchemesWithTheirOwnInstanceTitles()
    {
        // Two connections named alike enough to slugify to the same string ("Work"/"work!") — DepotMemorySource
        // falls the second back to its own connection id, so both still end up in the registry rather than one
        // silently losing the "scheme already taken" race.
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _AddRow(view);
        _SetRowFields(view, index: 1, name: "Work", url: "https://work-a.example.com");
        _SetRowFields(view, index: 2, name: "work!", url: "https://work-b.example.com");

        view.Save();

        Assert.Equal(3, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.work", out var first));
        Assert.Equal("Work", first!.InstanceTitle);
        var second = registry.Sources.Values.Single(registration => registration != first && registration.Scheme != "depot");
        Assert.Equal("work!", second.InstanceTitle);
        Assert.Equal("depot", second.FamilyKey);
    }

    [Fact]
    public void Save_BlankRow_IsDropped_AndContributesNothing()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);

        var saved = view.Save();

        Assert.True(saved);
        Assert.Empty(settings.Connections);
        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());
    }

    // --- AC-499: a row's own Sign-in action saves through this same Save() route before signing in ---------------

    [Fact]
    public async Task SignInAsync_RenamedRow_SignsInUnderTheNewStoredName_NotTheOldOne()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SignInMcpServerAsync("Depot: Work (renamed)", Arg.Any<CancellationToken>()).Returns(PluginMcpSignInOutcome.Authorized);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work (renamed)", url: "https://depot.example.com");
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().Single();

        await row.SignInAsync();

        _ = host.Received(1).SignInMcpServerAsync("Depot: Work (renamed)", Arg.Any<CancellationToken>());
        _ = host.DidNotReceive().SignInMcpServerAsync("Depot: Work", Arg.Any<CancellationToken>());
        Assert.Equal("Work (renamed)", settings.Connections.Single().Name);
        // The save that ran before sign-in reclaims the old registry entry through the existing route.
        _ = host.Received(1).RemoveMcpServer("Depot: Work");
    }

    // The gamble AC-499 exists to remove: a row whose own typed name collides with another row's already-kept one
    // must never sign in under its own ToRegistration().McpServerName, because Save() refused the whole batch, not
    // silently kept the other row and dropped this one — signing in under that computed name would authorize the
    // *other* row's connection under this row's belief.
    [Fact]
    public async Task SignInAsync_RowCollidesOnName_NeverSignsIn_AndLeavesBothRowsUnsaved()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: "Work", url: "https://first.example.com");
        _SetRowFields(view, index: 1, name: "Work", url: "https://second.example.com");
        var collidingRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);

        await collidingRow.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Neither row made it to storage — refusing the batch, not just dropping the second row.
        Assert.Empty(settings.Connections);
    }

    // Fix 2's actual failure scenario: two connections already stored and signed in before, one renamed into a
    // collision with the other. Pins the full end state a call-counting assert on "did not sign in" alone would
    // miss — both the stored connection list and the memory-source registry must come out exactly as they went in,
    // not with the colliding row's old entry silently reclaimed.
    [Fact]
    public async Task SignInAsync_RenameCollidesWithAnAlreadyStoredRow_RefusesTheWholeSave_AndLeavesStorageAndMemorySourcesUntouched()
    {
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com"),
                new DepotConnectionRegistration("conn-2", "Work2", "https://work2.example.com"),
            ],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "Work", url: "https://work2.example.com");
        var renamedRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);

        await renamedRow.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _ = host.DidNotReceive().RemoveMcpServer(Arg.Any<string>());
        Assert.Equal(2, settings.Connections.Count);
        Assert.Contains(settings.Connections, connection => connection.Id == "conn-1" && connection.Name == "Work");
        Assert.Contains(settings.Connections, connection => connection.Id == "conn-2" && connection.Name == "Work2");
        Assert.Equal(2, registry.Sources.Count);
        Assert.True(registry.Sources.ContainsKey("depot"));
        Assert.True(registry.Sources.ContainsKey("depot.work2"));
        Assert.Contains("Work", _AuthStatusText(renamedRow), StringComparison.Ordinal);
    }

    // AC-499 fix: _originalConnections must become each successful save's new reality, or a rename-then-rename-back
    // across two separate Sign-in saves on the same open view diffs the second save against the dialog's *opening*
    // snapshot instead of the first save's actual result — losing track of which scheme the connection now holds.
    [Fact]
    public async Task SignInAsync_TwoConsecutiveSaves_RenameThenRenameBack_EndStateHasOnlyTheCurrentScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Alpha", "https://alpha.example.com"),
                new DepotConnectionRegistration("conn-2", "Beta", "https://beta.example.com"),
            ],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "Gamma", url: "https://beta.example.com");
        var betaRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);
        await betaRow.SignInAsync();

        _SetRowFields(view, index: 1, name: "Beta", url: "https://beta.example.com");
        await betaRow.SignInAsync();

        Assert.Equal(2, registry.Sources.Count);
        Assert.True(registry.Sources.TryGetValue("depot.beta", out var beta));
        Assert.Equal("Beta", beta!.InstanceTitle);
        Assert.False(registry.Sources.ContainsKey("depot.gamma"));
    }

    // AC-499: the MCP-registry reclaim and memory-source sync that live in Save() run identically whether Save is
    // triggered by the host's own Save button or by a row's Sign-in click — checked here on the registry's actual
    // end state (FakeMemorySourceRegistry), the same reasoning Save_SwappingTwoConnectionNames_* above documents
    // for why a call-counting substitute cannot be trusted for this.
    [Fact]
    public async Task SignInAsync_RenamedRow_EndState_MemorySourceInstanceTitleFollowsTheRename()
    {
        var host = Substitute.For<ICockpitHost>();
        var registry = _WireRegistry(host);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Acme", "https://depot.example.com")],
        };
        foreach (var pair in DepotMemorySource.BuildRegistrationPairs(settings.Connections, host))
        {
            registry.Add(pair.Registration);
        }
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Acme (renamed)", url: "https://depot.example.com");
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().Single();

        await row.SignInAsync();

        Assert.True(registry.Sources.TryGetValue("depot", out var registration));
        Assert.Equal("Acme (renamed)", registration!.InstanceTitle);
    }

    // GetVisualDescendants only sees anything once the control is attached under a shown TopLevel — an unattached
    // tree has no realised visual children to walk, the same reason CanvasThemeRenderTests always shows a window
    // before it starts pulling controls out of one.
    private static void _Show(Control control)
    {
        // A control already attached under a shown window (a prior _Show/_AddRow/_SetRowFields call in the same
        // test) would throw "already has a visual parent" if wrapped in a second window — reuse the existing one.
        // Re-running UpdateLayout every time (not just on first attach) matters: a row added after the initial
        // layout pass has not had ApplyTemplate run on its own Content yet, so GetVisualDescendants would not find
        // its TextBoxes until another layout pass realises them.
        if (Avalonia.Controls.TopLevel.GetTopLevel(control) is Window existing)
        {
            existing.UpdateLayout();
            return;
        }

        var window = new Window { Content = control };
        window.Show();
        window.UpdateLayout();
    }

    private static string? _AuthStatusText(DepotConnectionRowControl row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Opacity == 0.8).Text;

    private static void _SetRowFields(DepotSettingsControl view, int index, string name, string url)
    {
        _Show(view);
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(index);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = name;
        boxes[1].Text = url;
    }

    private static void _AddRow(DepotSettingsControl view)
    {
        _Show(view);
        var add = view.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "+ Add connection"));
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    private static void _RemoveRow(DepotSettingsControl view, int index)
    {
        _Show(view);
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(index);
        var remove = row.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Remove connection"));

        // The row wires Click, not Command — raise the routed event RemoveRequested actually listens to.
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>
    /// A minimal stand-in for <c>ProjectMemorySourceRegistry</c> (internal to Cockpit.App, not referenced from this
    /// project) with the same first-one-wins-by-scheme rule, wired to the host substitute's
    /// <see cref="ICockpitHost.AddProjectMemorySource"/>/<see cref="ICockpitHost.RemoveProjectMemorySource"/> calls.
    /// Exists because a substitute that only counts calls cannot see an ordering bug where a later Add silently
    /// loses to an earlier connection's not-yet-retired scheme — only the registry's actual end state can.
    /// </summary>
    private sealed class FakeMemorySourceRegistry
    {
        private readonly Dictionary<string, ProjectMemorySourceRegistration> _sources = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ProjectMemorySourceRegistration> Sources => _sources;

        public void Add(ProjectMemorySourceRegistration registration) => _sources.TryAdd(registration.Scheme, registration);

        public void Remove(string scheme) => _sources.Remove(scheme);
    }
}
