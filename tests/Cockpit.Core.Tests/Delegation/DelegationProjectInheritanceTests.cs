using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// AC-320: a delegated task inherits the project of the session that delegated it, and carries it to the driver as a
/// value. Without that, a sub-agent doing a piece of its caller's work saw neither the MCP servers that project
/// brings (AC-218) nor what a plugin contributes to a session started under it (AC-165) — precisely where an agent
/// runs on its own and the project matters most.
/// </summary>
public class DelegationProjectInheritanceTests
{
    [Fact]
    public async Task DelegateAsync_FromASessionOnAProject_StartsTheTaskOnThatProject()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var projects = _ResolverSaying("pane-1", "cockpit");

        await _ServiceWith(driver, projects).DelegateAsync(new DelegationRequest("local", "work"), callerPaneId: "pane-1");

        await driver.Received().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            "cockpit", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelegateAsync_FromASessionWithoutAProject_StartsTheTaskWithoutOne()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var projects = _ResolverSaying("pane-1", null);

        await _ServiceWith(driver, projects).DelegateAsync(new DelegationRequest("local", "work"), callerPaneId: "pane-1");

        await driver.Received().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelegateAsync_OffTheVerifiedPath_AsksForNoProjectAtAll()
    {
        // No caller pane means no session to inherit from — and no lookup either, rather than one on a null pane.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var projects = _ResolverSaying("pane-1", "cockpit");

        await _ServiceWith(driver, projects).DelegateAsync(new DelegationRequest("local", "work"));

        await projects.DidNotReceive().ProjectIdOfAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await driver.Received().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            null, Arg.Any<CancellationToken>());
    }

    private static ISessionProjectResolver _ResolverSaying(string paneId, string? projectId)
    {
        var projects = Substitute.For<ISessionProjectResolver>();
        projects.ProjectIdOfAsync(paneId, Arg.Any<CancellationToken>()).Returns(projectId);
        return projects;
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static DelegationService _ServiceWith(ISessionDriver driver, ISessionProjectResolver projects)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: "acceptEdits"));

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30),
            workspaces: null,
            providerRegistry: null,
            projects: projects);
    }
}
