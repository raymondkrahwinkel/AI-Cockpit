using Avalonia.Controls;
using Cockpit.App.Docking;
using Material.Icons;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="DockPanelRegistry"/> (AC-951): first registration of an id wins, a later one with the same id is
/// refused. Empty until something registers: AC-951 seeded a placeholder to have a panel to open before the
/// Assistant was dockable, and AC-953 replaced it with the real one (AssistantIndicatorCoordinator.Start).
/// </summary>
public class DockPanelRegistryTests
{
    private static DockPanelRegistration _Panel(string id, string title = "T") =>
        new(id, title, MaterialIconKind.ViewDashboardOutline, () => new TextBlock());

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
