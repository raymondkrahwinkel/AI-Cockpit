using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// AC-128: a delegated task is scoped to the verified pane that created it. The task-addressed tools and list_tasks
/// take the caller's pane, so an agent cannot read, continue, stop, or even see another session's task by naming its
/// id (confused deputy). A null caller — the operator/UI, or the off-path in-process loop — stays unscoped.
/// </summary>
public class DelegationCallerScopingTests
{
    [Fact]
    public async Task ATask_IsReachableOnlyByThePaneThatCreatedIt_NotBySpoofingItsId()
    {
        var service = _ServiceWith(["/home/raymond/work"], _Target("qwen"));

        var task = await service.DelegateAsync(
            new DelegationRequest("qwen", "do work", WorkingDirectory: "/home/raymond/work"),
            callerPaneId: "owner-pane");

        // The owner reaches its own task.
        service.GetTask(task.TaskId, "owner-pane").Should().NotBeNull();
        service.ListTasks(callerPaneId: "owner-pane").Should().ContainSingle(view => view.TaskId == task.TaskId);

        // An attacker naming the id gets nothing — not the task, not its existence, and cannot stop it.
        service.GetTask(task.TaskId, "attacker-pane").Should().BeNull();
        service.ListTasks(callerPaneId: "attacker-pane").Should().BeEmpty();
        (await service.StopAsync(task.TaskId, "attacker-pane")).Should().BeNull();

        // The operator/UI (a null caller) is unscoped and still sees it.
        service.GetTask(task.TaskId).Should().NotBeNull();
    }

    private static SessionProfile _Target(string label) =>
        new(label, new ClaudeConfig(string.Empty), Delegation: new DelegationPolicy(AllowedAsTarget: true));

    private static DelegationService _ServiceWith(IReadOnlyList<string> workspaces, params SessionProfile[] profiles)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        var open = Substitute.For<ISessionWorkspaces>();
        open.ActiveWorkingDirectories.Returns(workspaces);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            open);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
