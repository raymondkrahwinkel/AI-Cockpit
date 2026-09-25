using Avalonia.Controls;
using NSubstitute;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubIssues.Tests;

// AC-1396: an `ICockpitUiHost` for the UI part's controls — an NSubstitute double whose channel is the given one and
// whose control-returning members hand back real (empty) controls, since a null child would fail the layout.
internal static class UiHostFake
{
    public static ICockpitUiHost Create(InProcessChannel? channel = null, IPluginStorage? storage = null)
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.Channel.Returns(channel ?? new InProcessChannel());
        host.Storage.Returns(storage ?? new InMemoryPluginStorage());
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        host.CreateMarkdownView(Arg.Any<string>())
            .Returns(call => new SelectableTextBlock { Text = call.Arg<string>(), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        return host;
    }
}
