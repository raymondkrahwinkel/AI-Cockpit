using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The session-banner contribution point (AC-802): a plugin registers one factory, and the cockpit builds it
/// once per session panel with that session's own context — the wider counterpart to
/// <see cref="SessionHeaderItemTests"/>'s header item, for PR/CI status and the like.
/// </summary>
public class SessionBannerItemTests
{
    [Fact]
    public void AddSessionBanner_RoutesToTheContributionSink()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        var host = NewHost(sink);
        Control Factory(IPluginSessionContext _) => new TextBlock();

        host.AddSessionBanner(Factory);

        sink.Received(1).AddPluginSessionBannerItem(Arg.Any<Func<IPluginSessionContext, Control>>());
    }

    // The default is a no-op, so a plugin built against this SDK still loads on a host that predates the
    // contribution point instead of failing at registration.
    [Fact]
    public void AHostWithoutTheContributionPoint_AcceptsTheRegistrationAndIgnoresIt()
    {
        var host = Substitute.ForPartsOf<HostWithoutBanners>();

        var register = () => ((ICockpitHost)host).AddSessionBanner(_ => new TextBlock());

        register();
    }

    private static ICockpitHost NewHost(IPluginContributionSink sink) =>
        new CockpitHost(
            "test-plugin",
            "Test Plugin",
            Substitute.For<IServiceProvider>(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

    /// <summary>An older host: implements only what the contract required before session banners existed.</summary>
    public abstract class HostWithoutBanners : ICockpitHost
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
