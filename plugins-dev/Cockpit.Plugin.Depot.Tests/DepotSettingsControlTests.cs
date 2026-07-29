using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
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
    private static void _Show(Control control)
    {
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

    private static void _RemoveRow(DepotSettingsControl view, int index)
    {
        _Show(view);
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(index);
        var remove = row.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Remove connection"));

        // The row wires Click, not Command — raise the routed event RemoveRequested actually listens to.
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}
