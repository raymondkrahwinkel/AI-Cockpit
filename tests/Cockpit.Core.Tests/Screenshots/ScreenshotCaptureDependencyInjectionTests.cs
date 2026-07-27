using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Screenshots;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// Guards the screen-capture platform switch (AC-220): building the real container the way <c>Program.cs</c>
/// does must resolve <see cref="IScreenshotCapture"/> the way <c>DependencyInjection.AddScreenshotCapture</c>
/// decides — the same per-platform pattern as the hotkey and the pty host. The capture itself (a live portal,
/// a Snip overlay, a privacy-gated helper) is out of unit-test reach; this is the purely testable part.
/// </summary>
/// <remarks>
/// It also proves each implementation can actually be built with what the container has, which a resolve
/// failure would otherwise only reveal the first time an operator pressed the key rather than at startup.
/// </remarks>
public class ScreenshotCaptureDependencyInjectionTests
{
    [Fact]
    public void Container_ResolvesThePlatformAppropriateScreenshotCapture()
    {
        using var provider = _BuildProvider();

        var capture = provider.GetService<IScreenshotCapture>();

        capture.Should().NotBeNull();
        capture.Should().BeOfType(_ExpectedForThisPlatform());
    }

    private static Type _ExpectedForThisPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return typeof(WindowsScreenshotCapture);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return typeof(MacScreenshotCapture);
        }

        // Linux takes the portal on X11 as well as Wayland — unlike the hotkey, there is no session split here.
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? typeof(PortalScreenshotCapture)
            : typeof(UnsupportedScreenshotCapture);
    }

    private static ServiceProvider _BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<SessionViewModel>>(
            provider => () => provider.GetRequiredService<SessionViewModel>());
        services.AddTransient<Func<TtyViewModel>>(
            provider => () => provider.GetRequiredService<TtyViewModel>());

        return services.BuildServiceProvider();
    }
}
