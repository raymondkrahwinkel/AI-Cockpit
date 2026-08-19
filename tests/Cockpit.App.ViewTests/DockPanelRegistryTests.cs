using Avalonia.Controls;
using Cockpit.App.Docking;
using Material.Icons;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="DockPanelRegistry"/> (AC-951): first registration of an id wins, a later one with the same id is
/// refused, and the seeded placeholder is there from construction so the rail has something to open and test
/// against before AC-950 [c] registers the Assistant.
/// </summary>
public class DockPanelRegistryTests
{
    private static DockPanelRegistration _Panel(string id, string title = "T") =>
        new(id, title, MaterialIconKind.ViewDashboardOutline, () => new TextBlock());

    [Fact]
    public void Constructor_SeedsThePlaceholderPanel()
    {
        var registry = new DockPanelRegistry();

        Assert.Single(registry.Panels);
        Assert.Equal("placeholder", registry.Panels[0].Id);
    }

    [Fact]
    public void Register_TheFirstOfAnId_Wins_AndALaterOneIsRefused()
    {
        var registry = new DockPanelRegistry();

        Assert.True(registry.Register(_Panel("assistant", "First")));
        Assert.False(registry.Register(_Panel("assistant", "Second")));
        Assert.Equal("First", registry.Panels.Single(panel => panel.Id == "assistant").Title);
    }

    [Fact]
    public void Register_RaisesChanged_SoALateArrivingPanelIsHeard()
    {
        var registry = new DockPanelRegistry();
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        registry.Register(_Panel("assistant"));

        Assert.Equal(1, raised);
    }
}
