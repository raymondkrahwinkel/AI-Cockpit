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
/// <see cref="DepotSettingsControl.Save"/> (AC-243): persists the connection list, and syncs the shared MCP
/// registry — a new/kept connection is (re)contributed under its <c>"Depot: &lt;name&gt;"</c> name, and a connection
/// that is gone or renamed has its <em>old</em> registry entry reclaimed so a stale contribution never lingers
/// (the orphan-cleanup KubernetesSettingsControl.Save does for a cluster's secret, applied to the registry instead).
/// </summary>
[Collection("avalonia")]
public class DepotSettingsControlTests
{
    [Fact]
    public void Save_NewConnection_ContributesItAsAnOAuthMcpServer_UnderThePrefixedName()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work", url: "https://depot.example.com");

        var saved = view.Save();

        Assert.True(saved);
        _ = host.Received(1).AddMcpServer(Arg.Is<McpServerContribution>(contribution =>
            contribution.Name == "Depot: Work"
            && contribution.Url == "https://depot.example.com/mcp"
            && contribution.OAuthAuthority == "https://depot.example.com"
            && contribution.OAuthClientId == null));
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

    // The guard this pins: AddMcpServer is an upsert-by-name, so a rename that only re-added under the new name
    // would leave the old name's entry (and whatever token is filed under it) behind forever.
    [Fact]
    public void Save_RenamedConnection_ReclaimsTheOldNameAndContributesTheNewOne()
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
        _ = host.Received(1).AddMcpServer(Arg.Is<McpServerContribution>(contribution => contribution.Name == "Depot: Work (new)"));
    }

    // The race Save() used to have: RemoveMcpServer and AddMcpServer each do their own load-modify-save round trip
    // with no locking across calls, so firing both at once meant the Add's read could land before the Remove's
    // write, and whichever SaveAsync finished last would silently win. Holding RemoveMcpServer's task open proves
    // the fix — Save() must not have started the Add while the Remove is still pending.
    [Fact]
    public async Task Save_RemovalAndAddition_AwaitsTheRemovalBeforeStartingTheAddition()
    {
        var host = Substitute.For<ICockpitHost>();
        var removeSignal = new TaskCompletionSource();
        host.RemoveMcpServer("Depot: Work").Returns(_ => removeSignal.Task);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work (new)", url: "https://depot.example.com");

        view.Save();

        _ = host.DidNotReceive().AddMcpServer(Arg.Any<McpServerContribution>());

        removeSignal.SetResult();
        await _WaitUntilAsync(() => host.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ICockpitHost.AddMcpServer)));

        _ = host.Received(1).AddMcpServer(Arg.Is<McpServerContribution>(contribution => contribution.Name == "Depot: Work (new)"));
    }

    // The guard this pins: two rows saved under the same name would upsert the very same registry entry from two
    // separate AddMcpServer calls — keep the first and drop the rest instead of letting the store end up holding
    // whichever row's URL happened to win.
    [Fact]
    public void Save_TwoRowsWithTheSameName_KeepsOnlyTheFirst_AndContributesOnce()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: "Work", url: "https://first.example.com");
        _SetRowFields(view, index: 1, name: "Work", url: "https://second.example.com");

        view.Save();

        _ = host.Received(1).AddMcpServer(Arg.Any<McpServerContribution>());
        _ = host.Received(1).AddMcpServer(Arg.Is<McpServerContribution>(contribution => contribution.Url == "https://first.example.com/mcp"));
        Assert.Single(settings.Connections);
    }

    // AC-501: memory sources sync the same save a connection's MCP contribution does, live, without an app restart.
    [Fact]
    public void Save_NewConnection_RegistersItsOwnMemorySourceUnderThePlainDepotScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Synvolution", url: "https://depot.example.com");

        view.Save();

        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Synvolution")));
    }

    [Fact]
    public void Save_SecondConnection_RegistersItUnderANamespacedScheme()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Synvolution", "https://depot.example.com")],
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
            Connections = [new DepotConnectionRegistration("conn-1", "Synvolution", "https://depot.example.com")],
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
            Connections = [new DepotConnectionRegistration("conn-1", "Synvolution", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Synvolution (renamed)", url: "https://depot.example.com");

        view.Save();

        host.Received(1).RemoveProjectMemorySource("depot");
        host.Received(1).AddProjectMemorySource(Arg.Is<ProjectMemorySourceRegistration>(registration =>
            registration.Scheme == "depot" && registration.Title.Contains("Synvolution (renamed)")));
    }

    [Fact]
    public void Save_UnchangedConnection_DoesNotReRegisterItsMemorySource()
    {
        // Re-adding unchanged content would only hit Register's "scheme already taken" refusal — this pins that
        // Save() does not even try, rather than relying on the registry to swallow a no-op call quietly.
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Synvolution", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);

        view.Save();

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
                new DepotConnectionRegistration("conn-1", "Synvolution", "https://depot.example.com"),
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

    // GetVisualDescendants only sees anything once the control is attached under a shown TopLevel — an unattached
    // tree has no realised visual children to walk, the same reason CanvasThemeRenderTests always shows a window
    // before it starts pulling controls out of one.
    // Save()'s MCP-registry sync runs as a discarded fire-and-forget task, so its continuation after
    // removeSignal.SetResult() is not guaranteed to have run by the very next line — this polls briefly instead of
    // assuming synchronous continuation timing, which is an implementation detail the test should not depend on.
    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

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
