using Avalonia.Controls;
using NSubstitute;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// An ICockpitUiHost substitute with the members that hand back a concrete value answering with one (AC-1396): a
// substitute's own default for a class is null, which a help hint added to a panel or a badge being written to
// cannot survive.
internal static class TestUiHost
{
    public static ICockpitUiHost Create(IPluginUiChannel? channel = null)
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.Channel.Returns(channel ?? new InProcessChannel());
        host.Storage.Returns(new InMemoryPluginStorage());
        host.AddSideMenuButtonWithBadge(Arg.Any<string>(), Arg.Any<Action>()).Returns(new SideMenuButtonBadge());
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new TextBlock());
        return host;
    }
}
