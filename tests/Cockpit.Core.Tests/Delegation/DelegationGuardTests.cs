using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Delegation (#67) spawns a real process under someone else's profile on the say-so of a model, so the guards
/// are the feature. These tests are about what the engine <em>refuses</em>: a profile that never opted in, a task
/// type it does not take, a working directory outside what it allows, a delegated task delegating on, and a
/// caller trying to run more at once than the profile's usage pot can carry.
/// </summary>
public class DelegationGuardTests
{
    [Fact]
    public async Task DelegateAsync_ToAProfileThatIsNotATarget_IsRefused()
    {
        // The default: a profile is not a delegation target until someone opts it in by hand.
        var service = _ServiceWith(new SessionProfile("private", new ClaudeConfig("/home/raymond/.claude")));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("private", "do work"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>()
            .WithMessage("*not available as a delegation target*");
    }

    [Fact]
    public async Task DelegateAsync_ToAnUnknownProfile_IsRefused()
    {
        // Only an existing profile can be a target: the driver, credentials and environment come from the
        // profile, so a free-form target would be a way to run anything.
        var service = _ServiceWith(_Target("local"));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("no-such-profile", "do work"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*No profile named*");
    }

    [Fact]
    public async Task DelegateAsync_WithATaskTypeTheProfileDoesNotAccept_IsRefused()
    {
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedTaskTypes = ["summarize"] }));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("local", "rm -rf", TaskType: "refactor"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*only accepts these task types*");
    }

    [Fact]
    public async Task DelegateAsync_WithAWorkingDirectoryOutsideThePolicy_IsRefused()
    {
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = ["/home/raymond/projects"] }));

        var delegate_ = async () => await service.DelegateAsync(
            new DelegationRequest("local", "read the secrets", WorkingDirectory: "/etc"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*does not allow a task to run in*");
    }

    [Fact]
    public async Task DelegateAsync_CannotWalkOutOfAnAllowedWorkingDirectory()
    {
        // The check resolves the path first, so a traversal that lands outside the allowed root is still outside.
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = ["/home/raymond/projects"] }));

        var delegate_ = async () => await service.DelegateAsync(
            new DelegationRequest("local", "escape", WorkingDirectory: "/home/raymond/projects/../../.ssh"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>();
    }

    [Fact]
    public async Task DelegateAsync_FromADelegatedTask_IsRefused_UnlessTheProfileAllowsIt()
    {
        // Without this a sub-agent handed the orchestrator tools could delegate in a loop.
        var service = _ServiceWith(_Target("local"));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("local", "and again", Depth: 1));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*may not delegate further*");
    }

    [Fact]
    public async Task DelegateAsync_BeyondTheProfilesConcurrencyCap_QueuesRatherThanSpawning()
    {
        // The cap protects the provider's usage pot. The honest answer is "queued" — not a silent refusal, and
        // certainly not starting it anyway.
        var service = _ServiceWith(_Target("local", policy => policy with { MaxConcurrent = 1 }));

        var first = await service.DelegateAsync(new DelegationRequest("local", "first"));
        var second = await service.DelegateAsync(new DelegationRequest("local", "second"));

        first.Status.Should().Be(DelegatedTaskStatus.Running);
        second.Status.Should().Be(DelegatedTaskStatus.Queued);
    }

    [Fact]
    public async Task ListTargetsAsync_HidesProfilesThatAreNotTargets()
    {
        // An agent cannot delegate to what it cannot see; the opted-out profile is simply absent.
        var service = _ServiceWith(
            new SessionProfile("private", new ClaudeConfig("/home/raymond/.claude")),
            _Target("local", policy => policy with { Purpose = "cheap bulk work", Tags = ["local", "cheap"] }));

        var targets = await service.ListTargetsAsync();

        targets.Should().ContainSingle();
        targets[0].ProfileLabel.Should().Be("local");
        targets[0].Purpose.Should().Be("cheap bulk work");
        targets[0].Tags.Should().Contain("cheap");
    }

    [Fact]
    public async Task StartedTask_RunsUnderTheProfilesPermissionCeiling_NotWhateverTheCallerWanted()
    {
        // A delegated session has nobody to answer a permission prompt, so it must not run in a mode that waits
        // for one — and "non-interactive" must not quietly become "bypass everything".
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local", policy => policy with { PermissionCeiling = "plan" }));

        await service.DelegateAsync(new DelegationRequest("local", "look around"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            "plan",
            Arg.Any<string?>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_WithoutAutoApprove_InstallsTheCeilingGate_NotBlanketAutoApprove()
    {
        // AC-79: a delegated local-model session is non-interactive (no human to answer a tool prompt). With the
        // profile's "Auto-Approve tool calls" off, it must gate each tool call against the ceiling + allow-list
        // rather than either hanging on a prompt or blanket-approving everything. _Target's default ceiling is
        // acceptEdits and it lists no tools.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local", policy => policy with { PermissionCeiling = "plan", AllowedTools = ["get_current_user"] }));

        await service.DelegateAsync(new DelegationRequest("local", "call a tool"));

        await driver.Received(1).SetDelegatedToolGateAsync(
            "plan",
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 1 && list.Contains("get_current_user")),
            Arg.Any<CancellationToken>());
        await driver.DidNotReceive().SetAutoApproveToolsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_WithAutoApproveOn_AllowsEverything_AndDoesNotInstallTheCeilingGate()
    {
        // The operator's per-profile "Auto-Approve tool calls" is the explicit "trust this profile fully": a
        // delegated session then allows every tool (still bounded by the enabled-server set), so it uses blanket
        // auto-approve and not the ceiling gate.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: true),
            Delegation: new DelegationPolicy(AllowedAsTarget: true));
        var service = _ServiceWith(driver, profile);

        await service.DelegateAsync(new DelegationRequest("local", "call a tool"));

        await driver.Received(1).SetAutoApproveToolsAsync(true, Arg.Any<CancellationToken>());
        await driver.DidNotReceive().SetDelegatedToolGateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_SendsThePromptToTheSession()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "summarise the changelog"));

        await driver.Received(1).SendUserMessageAsync(
            "summarise the changelog",
            Arg.Any<IReadOnlyList<Cockpit.Core.Sessions.ImageAttachment>?>(),
            Arg.Any<CancellationToken>());
        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Running);
    }

    [Fact]
    public async Task ADelegatedSession_KeepsItsOwnTools_ButNotTheOrchestrator()
    {
        // A sub-agent still needs its files, its shell, its git — withholding those would make delegation
        // useless. What it does not get is the orchestrator itself, so it cannot hand work on and start a chain.
        // This is the second lock on the recursion guard: no delegate_task tool, no chain, even if the depth
        // check were wrong.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(driverFactory, _Registry(), _Target("local"));

        await service.DelegateAsync(new DelegationRequest("local", "work"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers =>
                servers!.Contains("filesystem") && !servers.Contains("cockpit-orchestrator")),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADelegatedSession_GetsTheOrchestrator_WhenItsProfileMayDelegateFurther()
    {
        // The escape hatch is explicit and per profile: turn it on and that profile's tasks can delegate on.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(
            driverFactory,
            _Registry(),
            _Target("local", policy => policy with { MayDelegateFurther = true }));

        await service.DelegateAsync(new DelegationRequest("local", "work"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers => servers!.Contains("cockpit-orchestrator")),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendFollowUp_ReachesATaskThatHasAlreadyAnswered()
    {
        // A task that answered is Completed but its session is deliberately kept alive, so a follow-up must land
        // on it. This was broken: "finished" was read as "cannot take another turn", the message was dropped, and
        // the caller got a response that looked like success — so it waited for a turn that was never coming.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamCompletingATurn());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(driverFactory, _Registry(), _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "first turn"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Completed);

        // The whole point is the follow-up landing on an *answered* task, so assert that state before sending —
        // without this the test passes against the bug simply because the turn had not completed yet.
        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Completed);

        await service.SendFollowUpAsync(task.TaskId, "and now the tests");

        await driver.Received(1).SendUserMessageAsync("and now the tests", Arg.Any<IReadOnlyList<Cockpit.Core.Sessions.ImageAttachment>?>(), Arg.Any<CancellationToken>());
        service.GetTask(task.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Running);
    }

    [Fact]
    public async Task SendFollowUp_WhenTheProfileIsAlreadyAtItsCap_IsRefused()
    {
        // Found in live use: the cap gated new tasks but not follow-ups, so a follow-up woke a finished session
        // back up alongside a task that was already running on a profile set to one at a time — two models on the
        // same GPU, two draws on the same usage pot. The cap counts work being done, not tasks being started.
        // The first session answers and stays alive (so it could take a follow-up); the second keeps working, and
        // is therefore the profile's one allowed running task.
        var answering = Substitute.For<ISessionDriver>();
        answering.Events.Returns(_StreamCompletingATurn());
        var stillWorking = Substitute.For<ISessionDriver>();
        stillWorking.Events.Returns(_StreamThatNeverFinishes());

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(answering, stillWorking);
        var service = _Service(driverFactory, _Registry(), _Target("local", policy => policy with { MaxConcurrent = 1 }));

        var first = await service.DelegateAsync(new DelegationRequest("local", "first"));
        await _WaitUntilAsync(() => service.GetTask(first.TaskId)!.Status == DelegatedTaskStatus.Completed);

        var second = await service.DelegateAsync(new DelegationRequest("local", "second"));
        service.GetTask(second.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Running);

        var followUp = async () => await service.SendFollowUpAsync(first.TaskId, "one more thing");

        await followUp.Should().ThrowAsync<DelegationRejectedException>()
            .WithMessage("*already running as many tasks as it allows*");
        service.GetTask(first.TaskId)!.Status.Should().Be(DelegatedTaskStatus.Completed, "the refused follow-up must not put it back to work");
    }

    [Fact]
    public async Task SendFollowUp_ToATaskWhoseSessionIsGone_IsRefusedLoudly()
    {
        // The other half: never a quiet "ok" for a follow-up that cannot land.
        var service = _ServiceWith(_Target("local"));
        var task = await service.DelegateAsync(new DelegationRequest("local", "work"));
        await service.StopAsync(task.TaskId);

        var followUp = async () => await service.SendFollowUpAsync(task.TaskId, "more please");

        await followUp.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*no live session*");
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    // A session that is still working: it has produced nothing yet and its stream stays open, so the task sits at
    // Running — which is what occupies the profile's slot.
    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _StreamThatNeverFinishes()
    {
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        yield break;
    }

    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _StreamCompletingATurn()
    {
        yield return new Cockpit.Core.Sessions.AssistantTextCompleted { SessionId = "s1", Text = "here you go" };
        yield return new Cockpit.Core.Sessions.TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false };

        // The session stays open after the turn, exactly as a real driver's stream does while it waits for the
        // next message — if this completed, the runtime would look dead and the follow-up would have nowhere to go.
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    [Fact]
    public async Task AFailingStart_MarksTheTaskFailed_RatherThanLeavingItQueuedForever()
    {
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(_ => throw new InvalidOperationException("no such plugin provider"));
        var service = _Service(driverFactory, _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "work"));

        task.Status.Should().Be(DelegatedTaskStatus.Failed);
        task.Error.Should().Contain("no such plugin provider");
    }

    private static SessionProfile _Target(string label, Func<DelegationPolicy, DelegationPolicy>? tune = null)
    {
        var policy = new DelegationPolicy(AllowedAsTarget: true);
        return new SessionProfile(label, new ClaudeConfig(string.Empty), Delegation: tune?.Invoke(policy) ?? policy);
    }

    private static DelegationService _ServiceWith(params SessionProfile[] profiles)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        return _ServiceWith(driver, profiles);
    }

    private static DelegationService _ServiceWith(ISessionDriver driver, params SessionProfile[] profiles)
    {
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return _Service(driverFactory, profiles);
    }

    private static DelegationService _Service(ISessionDriverFactory driverFactory, params SessionProfile[] profiles) =>
        _Service(driverFactory, _Registry(), profiles);

    private static DelegationService _Service(
        ISessionDriverFactory driverFactory,
        IMcpServerStore mcpServerStore,
        params SessionProfile[] profiles)
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);
        return new DelegationService(profileStore, new SessionManager(driverFactory), mcpServerStore, Substitute.For<IDelegationAuditLog>(), NoSessionWorkspaces.Instance);
    }

    // The MCP registry as the operator configured it: their own servers, plus the orchestrator they switched on
    // for their main session.
    private static IMcpServerStore _Registry(params McpServerConfig[] servers)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(servers.Length > 0
            ? servers
            : [
                new McpServerConfig { Name = "filesystem", Enabled = true },
                new McpServerConfig { Name = "cockpit-orchestrator", Enabled = true },
            ]);
        return store;
    }

    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
