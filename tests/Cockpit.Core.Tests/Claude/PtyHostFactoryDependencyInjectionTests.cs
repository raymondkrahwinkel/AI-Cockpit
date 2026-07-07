using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Claude.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Guards the pty-host platform switch (#9 cross-platform increment): building the real container
/// the way <c>Program.cs</c> does must resolve <see cref="IPtyHostFactory"/> to the ConPTY
/// implementation on Windows and the Porta.Pty implementation everywhere else — a missing/wrong
/// registration would break every TTY-mode session's <c>ClaudeTtyLauncher</c> construction. The
/// actual pty spawn (real ConPTY/forkpty + a logged-in CLI) is out of unit-test reach; this is the
/// purely testable part, the branch itself.
/// </summary>
public class PtyHostFactoryDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<ClaudeSessionViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeSessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Container_ResolvesThePlatformAppropriatePtyHostFactory()
    {
        using var provider = BuildProvider();

        var factory = provider.GetService<IPtyHostFactory>();

        factory.Should().NotBeNull();
        factory.Should().BeOfType(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? typeof(ConPtyHostFactory)
            : typeof(PortaPtyHostFactory));
    }
}
