using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// AC-136: the delegating agent can narrow a task's MCP servers via <c>delegate_task</c>'s selection, bounded by
/// what the profile already allows. A request for a server outside the allowed set is refused up front (an
/// escalation attempt, never silently honoured); an allowed subset reaches the session; and list_profiles surfaces
/// the available servers per profile so the agent can choose a valid subset rather than guess.
/// </summary>
public class DelegationPerTaskMcpTests
{
    private const string Orchestrator = "cockpit-orchestrator";

    [Fact]
    public async Task DelegateAsync_RequestingTheOrchestratorWithoutMayDelegateFurther_IsRefused()
    {
        // The recursion guard: even though the orchestrator is enabled and in the profile's selection, a profile
        // that may not delegate further does not get it — so asking for it per task is an escalation and refused.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver,
            profileSelection: ["filesystem", Orchestrator],
            registry: [_Enabled("filesystem"), _Enabled(Orchestrator)]);

        var refuse = async () => await service.DelegateAsync(
            new DelegationRequest("local", "work", McpServers: [Orchestrator]));

        await refuse.Should().ThrowAsync<DelegationRejectedException>();
    }

    [Fact]
    public async Task DelegateAsync_AnEmptyPerTaskSelection_RunsWithTheProfileDefault_NotNoServers()
    {
        // [] is normalised to "no narrowing" (the profile's full set), not "no servers at all".
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver,
            profileSelection: ["filesystem", "youtrack"],
            registry: [_Enabled("filesystem"), _Enabled("youtrack")]);

        await service.DelegateAsync(new DelegationRequest("local", "work", McpServers: []));

        await driver.Received().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers => servers != null && servers.SetEquals(new HashSet<string> { "filesystem", "youtrack" })),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelegateAsync_WhenTheTaskRequestsAForbiddenServer_RefusesTheWholeCall()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver,
            profileSelection: ["filesystem", "youtrack"],
            registry: [_Enabled("filesystem"), _Enabled("youtrack"), _Enabled("git")]);

        var refuse = async () => await service.DelegateAsync(
            new DelegationRequest("local", "work", McpServers: ["git"]));

        (await refuse.Should().ThrowAsync<DelegationRejectedException>()).Which.Message.Should().Contain("git");
        await driver.DidNotReceive().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DelegateAsync_WithAnAllowedSubset_StartsTheSessionWithExactlyThatSubset()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver,
            profileSelection: ["filesystem", "youtrack"],
            registry: [_Enabled("filesystem"), _Enabled("youtrack")]);

        await service.DelegateAsync(new DelegationRequest("local", "work", McpServers: ["filesystem"]));

        await driver.Received().StartAsync(
            Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers => servers != null && servers.SetEquals(new HashSet<string> { "filesystem" })),
            Arg.Any<string?>(), Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListTargets_SurfacesTheMcpServersAProfileWouldGet()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver,
            profileSelection: ["filesystem", "youtrack"],
            registry: [_Enabled("filesystem"), _Enabled("youtrack"), _Enabled("git")]);

        var targets = await service.ListTargetsAsync();

        targets.Should().ContainSingle().Which.McpServers.Should().BeEquivalentTo("filesystem", "youtrack");
    }

    private static McpServerConfig _Enabled(string name) => new() { Name = name, Enabled = true };

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static DelegationService _ServiceWith(
        ISessionDriver driver, IReadOnlyList<string> profileSelection, McpServerConfig[] registry)
    {
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Delegation: new DelegationPolicy(AllowedAsTarget: true, PermissionCeiling: "acceptEdits"))
        {
            EnabledMcpServerNames = profileSelection,
        };

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([profile]);

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(registry);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            minutes => TimeSpan.FromMilliseconds(minutes * 30));
    }
}
