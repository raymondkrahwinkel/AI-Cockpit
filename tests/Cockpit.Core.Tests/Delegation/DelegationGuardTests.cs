using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
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
        var service = _ServiceWith(new SessionProfile("private", "/home/raymond/.claude"));

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
            new SessionProfile("private", "/home/raymond/.claude"),
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
            Arg.Any<CancellationToken>());
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
        return new SessionProfile(label, ConfigDir: string.Empty, Delegation: tune?.Invoke(policy) ?? policy);
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

    private static DelegationService _Service(ISessionDriverFactory driverFactory, params SessionProfile[] profiles)
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);
        return new DelegationService(profileStore, new SessionManager(driverFactory));
    }

    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
