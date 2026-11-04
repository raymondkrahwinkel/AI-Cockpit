using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Projects;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

public sealed class AssistantReadGatewayTests
{
    [Fact]
    public async Task ListSessions_NormalizesAndBoundsActiveToolActivity()
    {
        var session = Substitute.For<ISessionHandle>();
        session.ReadActivityAsync().Returns(Task.FromResult(("Bash\u001b[2J · " + new string('x', 130), "running 1:05")));
        session.HasOutstandingBackgroundShellsAsync().Returns(Task.FromResult(false));

        var sessions = Substitute.For<ISessionRegistry>();
        sessions.All.Returns([session]);
        var workspaces = Substitute.For<IWorkspaceSettingsStore>();
        workspaces.LoadAsync(Arg.Any<CancellationToken>()).Returns(WorkspaceSettings.Default);

        var gateway = new AssistantReadGateway(
            sessions,
            Substitute.For<ISharedProjectSourceRegistry>(),
            Substitute.For<IProjectStore>(),
            workspaces);

        var row = Assert.Single(await gateway.ListSessionsAsync());

        Assert.DoesNotContain('\u001b', row.Activity);
        Assert.True(row.Activity.Length <= 121);
        Assert.Equal("running 1:05", row.ActivityAge);
    }
}
