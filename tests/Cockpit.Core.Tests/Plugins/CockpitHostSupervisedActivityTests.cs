using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddSupervisedActivityProvider"/> (AC-82): a plugin's supervised-activity source reaches
/// the running UI through the contribution sink, the same wiring as the other status-bar/header contributions —
/// so the status bar can show its counter and its operator-only Kill panel.
/// </summary>
public class CockpitHostSupervisedActivityTests
{
    [Fact]
    public void AddSupervisedActivityProvider_ForwardsToTheContributionSink()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitHost host = _BuildHost(sink);
        var source = Substitute.For<ISupervisedActivitySource>();

        host.AddSupervisedActivityProvider(source);

        sink.Received(1).AddSupervisedActivityProvider(source);
    }

    private static CockpitHost _BuildHost(IPluginContributionSink sink) =>
        new(
            "kubernetes",
            "Kubernetes",
            new ServiceCollection().BuildServiceProvider(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);
}
