using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-802: the session-banner contribution point — the wider counterpart to
/// <see cref="SessionHeaderItemTests"/>'s header item.
/// </summary>
public class SessionBannerItemTests
{
    // The default is a no-op, so a plugin built against this SDK still loads on a host that predates the
    // contribution point instead of failing at registration.
    [Fact]
    public void AHostWithoutTheContributionPoint_AcceptsTheRegistrationAndIgnoresIt()
    {
        var host = Substitute.ForPartsOf<HostWithoutBanners>();

        var register = () => ((ICockpitHost)host).AddSessionBanner(_ => new TextBlock());

        register();
    }

    /// <summary>
    /// An older host: implements only what the contract required before session banners existed.
    /// </summary>
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
