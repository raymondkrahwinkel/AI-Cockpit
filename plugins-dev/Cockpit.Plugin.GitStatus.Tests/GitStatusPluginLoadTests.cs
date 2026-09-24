using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Cockpit.Plugin.GitStatus.UI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitStatus.Tests;

// What each part registers. The backend part (AC-1390): the workflow steps and the channel actions the UI part
// asks, and nothing with a window. The UI part: one settings view and one session-header item, and no side-menu
// button or section, whose absence is the regression AC-522 guards against.
public class GitStatusPluginLoadTests
{
    [Fact]
    public void Initialize_RegistersTheWorkflowStepsAndTheChannelActions_ButNothingWithAWindow()
    {
        var plugin = new GitStatusPlugin();
        plugin.ConfigureServices(new ServiceCollection());

        var host = new FakeCockpitHost(new FakeCockpitActions());
        plugin.Initialize(host);

        Assert.Equal(0, host.SettingsRegistered);
        Assert.Equal(0, host.SessionHeaderItemsRegistered);
        Assert.Equal(GitWorkflowSteps.All().Count(), host.WorkflowSteps.Count);
        Assert.Equal(["branch", "head-file", "status"], host.Bridge.Actions.Order());

        plugin.Dispose();

        Assert.Empty(host.Bridge.Actions);
    }

    [Fact]
    public void InitializeUi_RegistersTheSettingsAndTheHeaderItem_ButNoSideMenuButtonOrSection()
    {
        var host = Substitute.For<ICockpitUiHost>();

        new GitStatusUi().InitializeUi(host);

        host.Received(1).AddSettings(Arg.Any<Func<Control>>());
        host.Received(1).AddSessionHeaderItem(Arg.Any<Func<IPluginSessionContext, Control>>());
        host.DidNotReceive().AddSideMenuButton(Arg.Any<string>(), Arg.Any<Action>());
        host.DidNotReceive().AddSideMenuSection(Arg.Any<string>(), Arg.Any<Func<Control>>());
    }
}
