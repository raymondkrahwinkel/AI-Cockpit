using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>AC-691: the portal re-request button must show only where a portal is arming the hotkey — Linux+Wayland.</summary>
public class HotkeyPortalRetryVisibilityTests
{
    [Fact]
    public void LinuxWayland_ShowsTheButton() =>
        Assert.True(CockpitViewModel.ShouldShowHotkeyPortalRetry(isLinux: true, isWayland: true));

    [Fact]
    public void LinuxX11_DoesNotShowTheButton() =>
        Assert.False(CockpitViewModel.ShouldShowHotkeyPortalRetry(isLinux: true, isWayland: false));

    [Fact]
    public void NonLinux_DoesNotShowTheButton_EvenIfWaylandIsSomehowReported() =>
        Assert.False(CockpitViewModel.ShouldShowHotkeyPortalRetry(isLinux: false, isWayland: true));
}
