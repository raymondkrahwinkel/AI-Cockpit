using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The host side of inter-plugin intents (AC-95): <see cref="CockpitHost.SendIntent"/> and
/// <see cref="CockpitHost.RegisterIntentHandler"/> route through the shared <see cref="IPluginIntentRegistry"/>
/// resolved from the host's service provider, and the host stamps the calling plugin's own id on every intent it
/// sends — a plugin cannot dispatch under another's name, the same guarantee the consent gate gives.
/// </summary>
public class CockpitHostIntentTests
{
    private static CockpitHost HostFor(string pluginId, IServiceProvider services) =>
        new(
            pluginId,
            pluginId,
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);

    private static ServiceProvider SharedRegistry() =>
        new ServiceCollection().AddSingleton<IPluginIntentRegistry>(new PluginIntentRegistry()).BuildServiceProvider();

    [Fact]
    public async Task SendIntent_ReachesTheHandlerAnotherPluginRegistered_AndReturnsItsResult()
    {
        var services = SharedRegistry();
        HostFor("autopilot", services).RegisterIntentHandler("start", intent =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["session"] = "pane-" + intent.Data["issue"] }));

        var result = await HostFor("youtrack", services)
            .SendIntent("autopilot", "start", new Dictionary<string, string> { ["issue"] = "AC-95" });

        result.Should().NotBeNull();
        result!["session"].Should().Be("pane-AC-95");
    }

    [Fact]
    public async Task SendIntent_StampsTheCallersOwnId_NotAnythingTheCallerControls()
    {
        var services = SharedRegistry();
        PluginIntent? received = null;
        HostFor("autopilot", services).RegisterIntentHandler("start", intent =>
        {
            received = intent;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        });

        await HostFor("youtrack", services)
            .SendIntent("autopilot", "start", new Dictionary<string, string> { ["issue"] = "AC-95" });

        received.Should().NotBeNull();
        received!.CallerPluginId.Should().Be("youtrack");
        received.TargetPluginId.Should().Be("autopilot");
        received.Action.Should().Be("start");
        received.Data["issue"].Should().Be("AC-95");
    }

    [Fact]
    public async Task SendIntent_ReturnsNull_WhenTheTargetPluginIsNotListening()
    {
        var services = SharedRegistry();

        var result = await HostFor("youtrack", services)
            .SendIntent("autopilot", "start", new Dictionary<string, string>());

        result.Should().BeNull();
    }

    [Fact]
    public void CanSendIntent_IsTrueOnlyForARegisteredTargetAndAction()
    {
        var services = SharedRegistry();
        HostFor("autopilot", services).RegisterIntentHandler("start", _ =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        var caller = HostFor("youtrack", services);
        caller.CanSendIntent("autopilot", "start").Should().BeTrue();
        caller.CanSendIntent("autopilot", "stop").Should().BeFalse();
        caller.CanSendIntent("ghost", "start").Should().BeFalse();
    }
}
