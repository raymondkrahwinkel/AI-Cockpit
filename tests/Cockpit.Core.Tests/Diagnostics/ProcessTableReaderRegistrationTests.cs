using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Infrastructure;
using FluentAssertions;

namespace Cockpit.Core.Tests.Diagnostics;

/// <summary>
/// The resource meter's process-table reader must be the one for the platform it is running on (#78), and only
/// that one. This test exists because the first version got it wrong in the most embarrassing way available: the
/// three readers carried the Scrutor marker interface, so the assembly scan registered all three regardless of
/// platform, and Linux resolved the <em>Windows</em> WMI reader — which threw
/// <c>PlatformNotSupportedException</c> on startup and took the whole app down with it. The DI file even says, in
/// as many words, that the scan would do exactly that.
/// </summary>
public class ProcessTableReaderRegistrationTests
{
    [Fact]
    public void TheRegisteredReader_IsTheOneForThisPlatform_AndItRuns()
    {
        var services = _AsTheAppRegistersThem();

        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IProcessTableReader>();

        var expected = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "WmiProcessTableReader"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "PsProcessTableReader"
                : "ProcProcessTableReader";

        reader.GetType().Name.Should().Be(expected);

        // And it actually reads: a table with this very test process in it, which is the cheapest possible proof
        // that the platform path works at all rather than throwing.
        var rows = reader.Read();
        rows.Should().NotBeEmpty();
        rows.Should().Contain(row => row.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void OnlyOneReader_IsRegistered_SoTheScanCannotQuietlyBindTheWrongPlatformsOne()
    {
        _AsTheAppRegistersThem()
            .Count(service => service.ServiceType == typeof(IProcessTableReader))
            .Should().Be(1);
    }

    // The app registers the platform reader explicitly AND runs the Scrutor marker scan over this very assembly.
    // A test that only calls AddInfrastructure would miss the bug entirely: the scan is what bound all three.
    private static ServiceCollection _AsTheAppRegistersThem()
    {
        var services = new ServiceCollection();
        services.AddCore();
        services.AddInfrastructure();
        services.AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);

        return services;
    }
}
