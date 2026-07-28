using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.FanOut.Tests;

public class FanOutPluginTests
{
    [Fact]
    public void Initialize_OnAnyHost_ContributesTheFanOutWorkspaceType()
    {
        var host = Substitute.For<ICockpitHost>();
        WorkspaceTypeRegistration? registered = null;
        host.When(cockpit => cockpit.AddWorkspaceType(Arg.Any<WorkspaceTypeRegistration>()))
            .Do(call => registered = call.Arg<WorkspaceTypeRegistration>());

        new FanOutPlugin().Initialize(host);

        Assert.NotNull(registered);
        // The id is persisted with every workspace of this type; changing it orphans runs already set up.
        Assert.Equal("workspace.fanout", registered.Id);
        Assert.Equal("Fan-out", registered.Title);
    }

    [Fact]
    public void Metadata_Always_MatchesTheManifestTheHostLoadsBy()
    {
        Assert.Equal("fan-out", new FanOutPlugin().Metadata.Id);
    }
}
