using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Voice.GlobalHotkey;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Guards the global-push-to-talk platform switch (#34): building the real container the way
/// <c>Program.cs</c> does must resolve <see cref="IGlobalHotkeyService"/> to the XDG-portal
/// implementation on Linux and the SharpHook implementation on Windows — the same per-OS factory
/// pattern as <c>IPtyHostFactory</c> (see <c>PtyHostFactoryDependencyInjectionTests</c>). The real
/// portal/keyboard-hook wiring (a live compositor session, a real low-level hook) is out of
/// unit-test reach; this is the purely testable part, the branch itself.
/// </summary>
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
        service.Should().BeOfType(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? typeof(SharpHookGlobalHotkeyService)
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? typeof(PortalGlobalHotkeyService)
                : typeof(NoOpGlobalHotkeyService));
    }
}
