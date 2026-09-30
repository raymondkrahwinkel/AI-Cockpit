using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The widget contribution point: a plugin contributes a dashboard widget type (<c>ICockpitUiHost.AddWidget</c>),
/// and a Dashboard workspace's "Add widget" gallery reads them back through the host — the same shape as the
/// conversation-picker and workflow contribution points. The core hosts the grid and the pane chrome; what a
/// widget shows is the plugin's business.
/// </summary>
public class WidgetContributionTests
{
    /// <summary>
    /// Two plugins can claim one type id — nothing stops a third party picking one that already exists, and the
    /// cockpit's own clock did exactly that when it was split out of the reference-widgets plugin. Adding both
    /// put the type in the gallery twice and left CreateInstance resolving to whichever plugin happened to load
    /// first, which is not something an operator can see, let alone fix.
    /// </summary>
    [Fact]
    public void ASecondPluginClaimingTheSameWidgetType_IsRefusedRatherThanListedTwice()
    {
        var registry = new WidgetRegistry();

        NewHost(registry).AddWidget(new WidgetRegistration("widgets.clock", "Clock", _ => new Border()));
        NewHost(registry).AddWidget(new WidgetRegistration("widgets.clock", "Clock (the other one)", _ => new Border()));

        Assert.Equal("Clock", Assert.Single(registry.Widgets).Title);
    }

    private static ICockpitUiHost NewHost(IWidgetRegistry registry) =>
        TestUiHost.Create(services => services.AddSingleton(registry));
}
