using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The per-widget configuration block (Raymond, 2026-07-15: "elke widget kan ook zijn geheel eigen
/// configuratie blok bij zich hebben, dus hiervoor moet ook een settings knop toegevoegd worden per widget
/// die settings heeft"). Two things carry that: <see cref="WidgetRegistration.HasConfig"/> — the single fact
/// the ⚙ is bound to — and per-instance storage, so two of the same widget do not share their settings.
/// </summary>
public class WidgetConfigTests
{
    [Fact]
    public void HasConfig_IsFalse_WhenTheWidgetDeclaresNoSettingsForm()
    {
        // A clock has nothing to configure — and must therefore show no gear.
        var clock = new WidgetRegistration("clock.time", "Clock", _ => new TextBlock());

        clock.HasConfig.Should().BeFalse();
    }

    [Fact]
    public void HasConfig_IsTrue_WhenTheWidgetDeclaresASettingsForm()
    {
        var monitor = new WidgetRegistration("system-monitor.usage", "System Monitor", _ => new TextBlock())
        {
            CreateConfigView = _ => new TextBlock(),
        };

        monitor.HasConfig.Should().BeTrue();
    }

    [Fact]
    public void CreateConfigView_IsHandedTheSameInstanceContextAsTheView_SoTheFormWritesTheConfigTheViewReads()
    {
        IWidgetContext? viewContext = null;
        IWidgetContext? configContext = null;
        var registration = new WidgetRegistration("w", "W", context => { viewContext = context; return new TextBlock(); })
        {
            CreateConfigView = context => { configContext = context; return new TextBlock(); },
        };
        var context = _CreateContext("instance-1");

        registration.CreateView(context);
        registration.CreateConfigView!(context);

        viewContext.Should().BeSameAs(configContext);
    }

    [Fact]
    public void InstanceStorage_KeepsTwoInstancesOfTheSameWidgetApart()
    {
        // The case this exists for: two System Monitors on one dashboard, each with its own metrics.
        var plugin = _InMemoryStorage();
        var first = new WidgetContext("instance-1", plugin, Substitute.For<ICockpitSessionObserver>());
        var second = new WidgetContext("instance-2", plugin, Substitute.For<ICockpitSessionObserver>());

        first.Storage.Set("metrics", "cpu");
        second.Storage.Set("metrics", "ram");

        first.Storage.Get<string>("metrics").Should().Be("cpu");
        second.Storage.Get<string>("metrics").Should().Be("ram");
    }

    [Fact]
    public void InstanceStorage_DoesNotCollideWithThePluginsOwnTopLevelKeys()
    {
        // A plugin that contributes both a widget and a side-menu button shares one storage section.
        var plugin = _InMemoryStorage();
        var widget = new WidgetContext("instance-1", plugin, Substitute.For<ICockpitSessionObserver>());

        plugin.Set("metrics", "plugin-level");
        widget.Storage.Set("metrics", "widget-level");

        plugin.Get<string>("metrics").Should().Be("plugin-level");
        widget.Storage.Get<string>("metrics").Should().Be("widget-level");
    }

    [Fact]
    public void InstanceStorage_RoutesASecretThroughThePluginsSecretPath_SoItIsStillEncryptedAtRest()
    {
        var plugin = Substitute.For<IPluginStorage>();
        var widget = new WidgetContext("instance-1", plugin, Substitute.For<ICockpitSessionObserver>());

        widget.Storage.SetSecret("apiKey", "value");

        plugin.Received(1).SetSecret("widget:instance-1:apiKey", "value");
    }

    [Fact]
    public void RequestRefresh_ReachesTheWidgetThatOwnsTheInstance()
    {
        var context = _CreateContext("instance-1");
        var refreshed = 0;
        context.RefreshRequested += (_, _) => refreshed++;

        context.RequestRefresh();

        refreshed.Should().Be(1);
    }

    [Fact]
    public void RequestRefresh_WithNoListener_DoesNotThrow()
    {
        var act = () => _CreateContext("instance-1").RequestRefresh();

        act.Should().NotThrow();
    }

    private static WidgetContext _CreateContext(string instanceId) =>
        new(instanceId, _InMemoryStorage(), Substitute.For<ICockpitSessionObserver>());

    private static IPluginStorage _InMemoryStorage() =>
        new PluginStorage(new Dictionary<string, string>(), _ => { });
}
