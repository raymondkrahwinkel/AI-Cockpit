using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.StatusBar;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddSideMenuButtonWithBadge"/> (AC-516): a badge-carrying side-menu launcher forwards to
/// the contribution sink's badge-carrying overload, tagged with the plugin id like every other menu contribution —
/// and, unlike <see cref="CockpitHost.AddSideMenuButton"/>, hands the plugin back a live handle it keeps to update
/// the counter later.
/// </summary>
public class CockpitHostAddSideMenuButtonWithBadgeTests
{
    [Fact]
    public void AddSideMenuButtonWithBadge_ForwardsToTheContributionSink_TaggedWithThePluginId_CarryingTheBadge()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitHost host = _BuildHost(sink);
        Action onInvoke = () => { };

        var badge = host.AddSideMenuButtonWithBadge("Open PR's", onInvoke);

        sink.Received(1).AddPluginSideButton("github-pull-requests", "Open PR's", onInvoke, badge);
    }

    // The handle the plugin gets back is live: setting Primary/Secondary on it later must not require calling
    // AddSideMenuButtonWithBadge again (AC-516 acceptance criterion 1).
    [Fact]
    public void TheReturnedBadge_CanBeUpdatedAfterRegistration_WithoutReregistering()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitHost host = _BuildHost(sink);

        var badge = host.AddSideMenuButtonWithBadge("Open PR's", () => { });
        badge.Primary = 3;
        badge.Secondary = 2;

        Assert.Equal("3 / 2", badge.ToDisplayText());
        sink.Received(1).AddPluginSideButton(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Action>(), Arg.Any<SideMenuButtonBadge?>());
    }

    // AddSideMenuButton itself (AC-516 acceptance criterion 2) is untouched: it still goes through the plain
    // 3-argument sink member, never the badge-carrying one.
    [Fact]
    public void AddSideMenuButton_StillCallsThePlain3ArgSinkMember_NeverTheBadgeOne()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitHost host = _BuildHost(sink);
        Action onInvoke = () => { };

        host.AddSideMenuButton("Workflows", onInvoke);

        sink.Received(1).AddPluginSideButton("github-pull-requests", "Workflows", onInvoke);
        sink.DidNotReceive().AddPluginSideButton(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Action>(), Arg.Any<SideMenuButtonBadge?>());
    }

    // IPluginContributionSink.AddPluginSideButton(string, string, Action, SideMenuButtonBadge?) is a default
    // interface method that forwards to the plain 3-arg member — the whole point being that a sink which predates
    // AC-516 and only implements the mandatory 3-arg member still works when called through the new 4-arg path,
    // with no fakes across the repo needing to change.
    [Fact]
    public void ASinkThatOnlyImplementsThePlain3ArgMember_StillReceivesTheCall_ViaTheDefaultForward()
    {
        var sink = new _PreAc516Sink();
        ICockpitHost host = _BuildHost(sink);
        Action onInvoke = () => { };

        var badge = host.AddSideMenuButtonWithBadge("Open PR's", onInvoke);

        var (pluginId, title, forwardedInvoke) = Assert.Single(sink.Buttons);
        Assert.Equal("github-pull-requests", pluginId);
        Assert.Equal("Open PR's", title);
        Assert.Same(onInvoke, forwardedInvoke);
        Assert.NotNull(badge); // the plugin still gets a real handle back, even though this sink never renders it
    }

    private static CockpitHost _BuildHost(IPluginContributionSink sink) =>
        new(
            "github-pull-requests",
            "GitHub Pull Requests",
            new ServiceCollection().BuildServiceProvider(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

    // Deliberately implements only the members IPluginContributionSink actually mandates (everything else on the
    // interface is default-implemented) — the minimal shape a pre-AC-516 fake sink would have had.
    private sealed class _PreAc516Sink : IPluginContributionSink
    {
        public List<(string PluginId, string Title, Action OnInvoke)> Buttons { get; } = [];

        public void AddPluginSideSection(string pluginId, string title, Func<Control> createView)
        {
        }

        public void AddPluginSideButton(string pluginId, string title, Action onInvoke) =>
            Buttons.Add((pluginId, title, onInvoke));

        public void AddPluginSessionHeaderItem(Func<IPluginSessionContext, Control> createView)
        {
        }

        public void AddPluginSessionHeaderAction(PluginSessionAction action)
        {
        }

        public void AddSupervisedActivityProvider(ISupervisedActivitySource source)
        {
        }

        public void AddToolbarAction(string pluginId, ToolbarAction action)
        {
        }

        public void AddPluginShortcut(PluginShortcut shortcut)
        {
        }

        public void AddPluginSettings(string pluginId, string pluginName, Func<Control> createView)
        {
        }

        public bool HasPluginSettings(string pluginId) => false;

        public Task OpenPluginSettingsAsync(string pluginId) => Task.CompletedTask;

        public void AddSettingsSavedHandler(string pluginId, Action callback)
        {
        }

        public void NotifySettingsSaved(string pluginId)
        {
        }

        public void ApplyPluginMenuPreference(string pluginId, int menuOrder, bool hiddenInMenu)
        {
        }
    }
}
