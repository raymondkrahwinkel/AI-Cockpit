using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The companion-tool contribution point's registry: a plugin registers a mini-tool
/// (<c>ICockpitHost.AddCompanionTool</c>) and the cockpit's pop-out companion window reads them back — the same
/// shape as <see cref="IWidgetRegistry"/>.
/// </summary>
public class CompanionToolRegistryTests
{
    private static IPluginStorage _InMemoryStorage() => new PluginStorage(new Dictionary<string, string>(), _ => { });

    /// <summary>Two plugins can claim one tool id — the first one wins, the second is refused rather than listed beside it.</summary>
    [Fact]
    public void ASecondPluginClaimingTheSameToolId_IsRefusedRatherThanListedTwice()
    {
        var registry = new CompanionToolRegistry();
        registry.Register(
            new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        var registeredAgain = registry.Register(
            new CompanionToolRegistration("tools.clock", "Clock (the other one)", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        Assert.False(registeredAgain);
        Assert.Equal("Clock", Assert.Single(registry.Tools).Title);
    }

}
