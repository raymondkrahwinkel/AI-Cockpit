using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The session-header contribution point (#: session header items): a plugin registers one factory, and the
/// cockpit builds it once per session panel with that session's own context — so an indicator in a session's
/// header describes the session it sits in, not whichever one happens to be selected.
/// </summary>
public class SessionHeaderItemTests
{
    [Fact]
    public void AddSessionHeaderItem_RoutesToTheContributionSink()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        var host = NewHost(sink);
        Control Factory(IPluginSessionContext _) => new TextBlock();

        host.AddSessionHeaderItem(Factory);

        sink.Received(1).AddPluginSessionHeaderItem(Arg.Any<Func<IPluginSessionContext, Control>>());
    }

    // The default is a no-op, so a plugin built against this SDK still loads on a host that predates the
    // contribution point instead of failing at registration.
    [Fact]
    public void AHostWithoutTheContributionPoint_AcceptsTheRegistrationAndIgnoresIt()
    {
        var host = Substitute.ForPartsOf<HostWithoutHeaderItems>();

        var register = () => ((ICockpitHost)host).AddSessionHeaderItem(_ => new TextBlock());

        register.Should().NotThrow();
    }

    private static ICockpitHost NewHost(IPluginContributionSink sink) =>
        new CockpitHost(
            "test-plugin",
            Substitute.For<IServiceProvider>(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);

    /// <summary>An older host: implements only what the contract required before header items existed.</summary>
    public abstract class HostWithoutHeaderItems : ICockpitHost
    {
        public IServiceProvider Services => Substitute.For<IServiceProvider>();

        public ICockpitActions Actions => Substitute.For<ICockpitActions>();

        public IPluginStorage Storage => Substitute.For<IPluginStorage>();

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;
    }
}
