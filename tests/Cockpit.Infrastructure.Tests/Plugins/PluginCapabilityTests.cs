using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Capabilities;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Infrastructure.Tests.Plugins;

// AC-1730: the host hands the operator's own time only to a plugin whose manifest asks for it.
public class PluginCapabilityTests
{
    [Fact]
    public void TimeEntries_IsRefusedWithTheMissingCapability_UnlessTheManifestAsksForIt()
    {
        var entries = Substitute.For<ICockpitTimeEntries>();

        var without = Assert.Throws<UnauthorizedAccessException>(() => _HostFor("", entries).TimeEntries);
        Assert.Contains(CapabilityCatalog.TimeEntries, without.Message);

        Assert.Same(entries, _HostFor(""","capabilities":["time.entries"]""", entries).TimeEntries);
    }

    private static PluginBackendHost _HostFor(string capabilities, ICockpitTimeEntries entries)
    {
        var json = $$"""{"id":"productivity","name":"Productivity","version":"1.0.0","entryAssembly":"P.dll","abstractionsVersion":3{{capabilities}}}""";
        Assert.True(PluginManifest.TryParse(json, out var manifest, out _));
        var registry = new SessionRegistry();
        return new PluginBackendHost(
            "productivity",
            "Productivity",
            new ServiceCollection().AddSingleton(entries).BuildServiceProvider(),
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            new PluginBackendSessionObserver(registry),
            new PluginBackendActions(Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>()),
            new PluginDiagnostics(),
            capabilities: manifest!.Capabilities);
    }
}
