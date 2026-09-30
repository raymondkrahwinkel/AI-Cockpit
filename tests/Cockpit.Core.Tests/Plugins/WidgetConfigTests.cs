using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
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
    public void InstanceStorage_RoutesASecretThroughThePluginsSecretPath_SoItIsStillEncryptedAtRest()
    {
        var plugin = Substitute.For<IPluginStorage>();
        var widget = new WidgetContext("instance-1", plugin, Substitute.For<ICockpitSessionObserver>());

        widget.Storage.SetSecret("apiKey", "value");

        plugin.Received(1).SetSecret("widget:instance-1:apiKey", "value");
    }

}
