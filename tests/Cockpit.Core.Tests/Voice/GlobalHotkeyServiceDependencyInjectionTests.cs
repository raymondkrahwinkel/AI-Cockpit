using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Voice.GlobalHotkey;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Guards the global-push-to-talk platform switch (#34): building the real container the way
/// <c>Program.cs</c> does must resolve <see cref="IGlobalHotkeyService"/> the way
/// <c>DependencyInjection.AddGlobalHotkey</c> decides — the same per-platform factory pattern as
/// <c>IPtyHostFactory</c> (see <c>PtyHostFactoryDependencyInjectionTests</c>). The real portal/keyboard-hook
/// wiring (a live compositor session, a real low-level hook) is out of unit-test reach; this is the purely
/// testable part, the branch itself.
/// </summary>
/// <remarks>
/// This used to assert "Linux gets the portal", which is the rule <c>f348014</c> deliberately replaced: under
/// Wayland nothing may install a keyboard hook so the portal is the only route, but under X11 the hook works and
/// routing every Linux to the portal cost those desktops the hotkey outright. The test kept the old rule and went
/// red the first time it ran anywhere that is Linux but not Wayland — which is every CI runner, and which is why
/// <c>main</c> was red from <c>f348014</c> until anyone looked at the log.
/// </remarks>
public class GlobalHotkeyServiceDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<SessionViewModel>>(
            provider => () => provider.GetRequiredService<SessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Container_ResolvesThePlatformAppropriateGlobalHotkeyService()
    {
        using var provider = BuildProvider();

        var service = provider.GetService<IGlobalHotkeyService>();

        service.Should().NotBeNull();
        service.Should().BeOfType(_ExpectedForThisSession());
    }

    /// <summary>
    /// What this machine should get: Windows takes the keyboard hook, Linux is decided by the session rather than
    /// the OS, and anything else (macOS) has neither and says so. What the session means is asked of
    /// <see cref="LinuxSession"/> — the same kernel the registration asks, and the reason this file no longer
    /// carries a second copy of that rule to drift from the first. <see cref="LinuxSessionTests"/> is where both
    /// of its answers are actually proved; a headless CI runner can only ever be the X11 one.
    /// </summary>
    private static Type _ExpectedForThisSession()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return typeof(SharpHookGlobalHotkeyService);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return typeof(NoOpGlobalHotkeyService);
        }

        return LinuxSession.IsWayland(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
            ? typeof(PortalGlobalHotkeyService)
            : typeof(SharpHookGlobalHotkeyService);
    }
}
