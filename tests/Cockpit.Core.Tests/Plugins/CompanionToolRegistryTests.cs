using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.CompanionTools;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The companion-tool contribution point's registry: a plugin registers a mini-tool
/// (<c>ICockpitHost.AddCompanionTool</c>) and the cockpit's pop-out companion window reads them back — the same
/// shape as <see cref="IWidgetRegistry"/>.
/// </summary>
public class CompanionToolRegistryTests
{
    [Fact]
    public void ARegisteredTool_BecomesAvailableInTheRegistry()
    {
        var registry = new CompanionToolRegistry();

        var registered = registry.Register(new CompanionToolRegistration("assistant.indicator", "Assistant", _ => new Border()));

        Assert.True(registered);
        Assert.Equal("assistant.indicator", Assert.Single(registry.Tools).Id);
    }

    /// <summary>Two plugins can claim one tool id — the first one wins, the second is refused rather than listed beside it.</summary>
    [Fact]
    public void ASecondPluginClaimingTheSameToolId_IsRefusedRatherThanListedTwice()
    {
        var registry = new CompanionToolRegistry();
        registry.Register(new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()));

        var registeredAgain = registry.Register(new CompanionToolRegistration("tools.clock", "Clock (the other one)", _ => new Border()));

        Assert.False(registeredAgain);
        Assert.Equal("Clock", Assert.Single(registry.Tools).Title);
    }

    [Fact]
    public void Register_RaisesChanged()
    {
        var registry = new CompanionToolRegistry();
        var raised = false;
        registry.Changed += (_, _) => raised = true;

        registry.Register(new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()));

        Assert.True(raised);
    }
}
