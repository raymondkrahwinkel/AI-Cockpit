using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-691: the portal re-request button only makes sense where a portal is what's arming the hotkey — Wayland,
/// via <c>PortalGlobalHotkeyService</c>. X11 and Windows use a keyboard hook with no portal permission to lose,
/// and macOS has no global hotkey at all. The platform/session gate is a hard runtime check on the view model, so
/// the decision is pulled out to be testable on any OS.
/// </summary>
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
