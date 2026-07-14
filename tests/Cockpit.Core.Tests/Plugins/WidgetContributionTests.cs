using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The widget contribution point: a plugin contributes a dashboard widget type (<c>ICockpitHost.AddWidget</c>),
/// and a Dashboard workspace's "Add widget" gallery reads them back through the host — the same shape as the
/// conversation-picker and workflow contribution points. The core hosts the grid and the pane chrome; what a
/// widget shows is the plugin's business.
/// </summary>
public class WidgetContributionTests
{
    [Fact]
    public void APluginWidget_BecomesAvailableToTheDashboardGallery()
    {
        var registry = new WidgetRegistry();
        var host = NewHost(registry);

        host.AddWidget(new WidgetRegistration("system-monitor.usage", "System Monitor", _ => new Border())
        {
            Icon = "📈",
            Description = "Live CPU / RAM / disk.",
            DefaultColumnSpan = 2,
        });

        host.Widgets.Should().ContainSingle().Which.Id.Should().Be("system-monitor.usage");
        registry.Widgets.Should().ContainSingle().Which.DefaultColumnSpan.Should().Be(2);
    }

    [Fact]
    public void WidgetRegistration_DefaultsToASingleCellNeutralCard()
    {
        var registration = new WidgetRegistration("plugin.clock", "Clock", _ => new Border());

        registration.Icon.Should().Be("🧩");
        registration.DefaultColumnSpan.Should().Be(1);
        registration.DefaultRowSpan.Should().Be(1);
        registration.Description.Should().BeEmpty();
    }

    // No widget-providing plugin installed is the normal case: the gallery is simply empty.
    [Fact]
    public void WithNoWidgetPluginInstalled_TheGalleryIsEmpty()
    {
        new WidgetRegistry().Widgets.Should().BeEmpty();
    }

    private static ICockpitHost NewHost(IWidgetRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);

        return new CockpitHost(
            "test-plugin",
            "Test Plugin",
            services.BuildServiceProvider(),
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);
    }
}
