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

// AC-1730: the host hands the operator's own time only to a plugin whose manifest asks for it; AC-1720 the
// timeline of every session the same way.
public class PluginCapabilityTests
{
    [Fact]
    public void TimeEntriesAndTimeline_AreRefusedWithTheMissingCapability_UnlessTheManifestAsksForThem()
    {
        var entries = Substitute.For<ICockpitTimeEntries>();
        var timeline = Substitute.For<ICockpitTimelineReader>();

        var without = Assert.Throws<UnauthorizedAccessException>(() => _HostFor("", entries, timeline).TimeEntries);
        Assert.Contains(CapabilityCatalog.TimeEntries, without.Message);
        var withoutTimeline = Assert.Throws<UnauthorizedAccessException>(() => _HostFor(""","capabilities":["time.entries"]""", entries, timeline).Timeline);
        Assert.Contains(CapabilityCatalog.TimelineReadAll, withoutTimeline.Message);

        Assert.Same(entries, _HostFor(""","capabilities":["time.entries"]""", entries, timeline).TimeEntries);
        Assert.Same(timeline, _HostFor(""","capabilities":["timeline.read-all"]""", entries, timeline).Timeline);
    }

    private static PluginBackendHost _HostFor(string capabilities, ICockpitTimeEntries entries, ICockpitTimelineReader timeline)
    {
        var json = $$"""{"id":"productivity","name":"Productivity","version":"1.0.0","entryAssembly":"P.dll","abstractionsVersion":3{{capabilities}}}""";
        Assert.True(PluginManifest.TryParse(json, out var manifest, out _));
        Assert.NotNull(manifest);
        var registry = new SessionRegistry();
        return new PluginBackendHost(
            "productivity",
            "Productivity",
            new ServiceCollection().AddSingleton(entries).AddSingleton(timeline).BuildServiceProvider(),
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            new PluginBackendSessionObserver(registry),
            new PluginBackendActions(Substitute.For<ISessionProfileStore>(), Substitute.For<IDelegationService>()),
            new PluginDiagnostics(),
            capabilities: manifest.Capabilities);
    }
}
