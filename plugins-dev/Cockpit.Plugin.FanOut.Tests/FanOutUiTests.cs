using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutUiTests
{
    [Fact]
    public void InitializeUi_OnAnyHost_ContributesTheFanOutWorkspaceType()
    {
        var host = Substitute.For<ICockpitUiHost>();
        WorkspaceTypeRegistration? registered = null;
        host.When(cockpit => cockpit.AddWorkspaceType(Arg.Any<WorkspaceTypeRegistration>()))
            .Do(call => registered = call.Arg<WorkspaceTypeRegistration>());

        new FanOutUi().InitializeUi(host);

        Assert.NotNull(registered);
        // The id is persisted with every workspace of this type; changing it orphans runs already set up.
        Assert.Equal("workspace.fanout", registered.Id);
        Assert.Equal("Fan-out", registered.Title);
    }
}
