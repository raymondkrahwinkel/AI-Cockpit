using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Every MCP endpoint the cockpit registers can actually be built from the container it will be built from.
/// </summary>
/// <remarks>
/// <b>Why this exists.</b> <c>CockpitMcpEndpointHost</c> creates each endpoint's tools class with
/// <c>ActivatorUtilities.CreateInstance</c> inside a try/catch that logs a warning and moves on. So an endpoint whose
/// dependency is not registered does not crash anything: the server simply never mounts, the tools are silently
/// absent, and the only trace is a line in a log file nobody is reading. AC-545 shipped in exactly that state for a
/// while — the acting tools were registered, hosted and covered by tests, and no implementation of
/// <c>IAssistantAgentGateway</c> existed at all. Everything compiled and every test stayed green.
/// <para>
/// It checks registration rather than resolving for real, deliberately. Constructing the assistant's tools would
/// drag in <c>CockpitViewModel</c> and with it the whole application graph — settings files, plugin discovery, the
/// session machinery — which is a different test with different failure modes. What is asserted here is precisely
/// the thing that broke: a constructor parameter with nobody registered to fill it.
/// </para>
/// </remarks>
public class McpEndpointResolvabilityTests
{
    [Fact]
    public void EveryRegisteredEndpoint_HasAToolsClassWhoseDependenciesAreAllRegistered()
    {
        // The same composition Program.Main builds, with the same three assemblies — a test that scanned fewer
        // would report a gap the app does not have, and one that scanned more would hide a gap it does.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(Cockpit.App.Program).Assembly);

        var registered = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<CockpitMcpEndpoint>()
            .ToList();

        // Derived from the registrations, so a fourth endpoint is covered the day it is added and a query that
        // suddenly finds nothing fails here rather than passing vacuously.
        Assert.NotEmpty(registered);

        var missing = new List<string>();
        foreach (var endpoint in registered)
        {
            foreach (var parameter in endpoint.ToolsType.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                var known = services.Any(descriptor =>
                    descriptor.ServiceType == parameter.ParameterType
                    || (parameter.ParameterType.IsGenericType
                        && descriptor.ServiceType == parameter.ParameterType.GetGenericTypeDefinition()));

                if (!known && !parameter.HasDefaultValue)
                {
                    missing.Add($"{endpoint.ServerName} needs {parameter.ParameterType.Name}, which nothing registers");
                }
            }
        }

        Assert.Empty(missing);
    }
}
