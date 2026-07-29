using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The session question the global-hotkey registration turns on (#34). Read straight from the environment this was
/// untestable anywhere but the session itself: every CI runner sets neither variable, so only the X11 answer was
/// ever exercised and the Wayland one rested on the comment above it. As a kernel both answers are reachable from
/// any machine, which is the point of it being one.
/// </summary>
public class LinuxSessionTests
{
    [Theory]
    [InlineData("wayland")]
    [InlineData("Wayland")]
    [InlineData("WAYLAND")]
    public void TheSessionSaysItIsWayland_SoItIs(string sessionType)
    {
        Assert.True(LinuxSession.IsWayland(sessionType, waylandDisplay: null));
    }

    /// <summary>Raymond's KDE/Wayland laptop, the machine the portal route was designed against.</summary>
    [Fact]
    public void AWaylandSessionThatSetsBoth_IsWayland()
    {
        Assert.True(LinuxSession.IsWayland("wayland", "wayland-0"));
    }

    /// <summary>The socket is the fallback for a session that never set the type — a compositor is being talked to either way.</summary>
    [Fact]
    public void OnlyTheSocketIsSet_StillWayland()
    {
        Assert.True(LinuxSession.IsWayland(xdgSessionType: null, waylandDisplay: "wayland-0"));
    }

    [Fact]
    public void AnX11Session_IsNotWayland()
    {
        Assert.False(LinuxSession.IsWayland("x11", waylandDisplay: null));
    }

    /// <summary>
    /// The case that decided this was worth a kernel: X11 is where routing every Linux to the portal cost the
    /// hotkey outright, and it is also the only case a CI runner can reach. Both arms need to be provable from one
    /// machine or neither is.
    /// </summary>
    [Fact]
    public void AnX11SessionWithNoWaylandSocket_IsNotWayland()
    {
        Assert.False(LinuxSession.IsWayland("x11", waylandDisplay: ""));
    }

    /// <summary>A headless session — a CI runner, a container — sets neither. X11 is the honest read: there is no compositor to ask.</summary>
    [Fact]
    public void ASessionThatSaysNothing_IsNotWayland()
    {
        Assert.False(LinuxSession.IsWayland(xdgSessionType: null, waylandDisplay: null));
    }

    [Fact]
    public void ATtySession_IsNotWayland()
    {
        Assert.False(LinuxSession.IsWayland("tty", waylandDisplay: null));
    }
}
