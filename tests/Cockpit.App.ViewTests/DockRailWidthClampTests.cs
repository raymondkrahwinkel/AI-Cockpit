using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

public sealed class DockRailWidthClampTests
{
    [Theory]
    [InlineData(1940, 2560, 180, 1940)]
    [InlineData(1940, 900, 180, 280)]
    [InlineData(600, 1000, 280, 280)]
    [InlineData(200, 2560, 180, 280)]
    public void ClampDockRailWidth_KeepsTheWorkspaceWithinItsMinimum(double preferredWidth, double windowWidth, double sidebarWidth, double expectedWidth)
    {
        Assert.Equal(expectedWidth, CockpitViewModel.ClampDockRailWidth(preferredWidth, windowWidth, sidebarWidth));
    }
}
