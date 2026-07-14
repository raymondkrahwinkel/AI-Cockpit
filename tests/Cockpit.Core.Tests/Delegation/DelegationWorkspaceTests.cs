using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Where a delegated task may run (#67). A profile's own allow-list governs the disk at large — but the directory the
/// <em>delegating session</em> is already working in is allowed without it: you delegate from a session in a
/// repository, that session can already read and write there, and refusing its sub-agent the same directory made
/// delegation useless in the only place anyone does it. It reaches nothing the caller did not already have.
/// </summary>
public class DelegationWorkspaceTests
{
    [Fact]
    public async Task ADirectoryASessionIsWorkingIn_IsAllowed_WithoutTheProfileNamingIt()
    {
        // The profile allows nothing of its own — which used to mean "nowhere", and refused every delegation that
        // carried a working directory at all.
        var service = _ServiceWith(
            workspaces: ["/home/raymond/RiderProjects/Eveworkbench"],
            _Target("qwen"));

        var task = await service.DelegateAsync(
            new DelegationRequest("qwen", "review the frontend diff", WorkingDirectory: "/home/raymond/RiderProjects/Eveworkbench"));

        task.Status.Should().NotBe(DelegatedTaskStatus.Failed);
    }

    [Fact]
    public async Task ADirectoryUnderOneASessionIsWorkingIn_IsAllowedToo()
    {
        var service = _ServiceWith(
            workspaces: ["/home/raymond/RiderProjects/Eveworkbench"],
            _Target("qwen"));

        var task = await service.DelegateAsync(
            new DelegationRequest("qwen", "look at the frontend", WorkingDirectory: "/home/raymond/RiderProjects/Eveworkbench/src"));

        task.Status.Should().NotBe(DelegatedTaskStatus.Failed);
    }

    // The grant is the caller's own reach, not a way around the policy: somewhere no session of yours is, and the
    // profile does not allow, is still refused.
    [Fact]
    public async Task ADirectoryNoSessionIsIn_IsStillRefused()
    {
        var service = _ServiceWith(
            workspaces: ["/home/raymond/RiderProjects/Eveworkbench"],
            _Target("qwen"));

        var delegate_ = async () => await service.DelegateAsync(
            new DelegationRequest("qwen", "read the secrets", WorkingDirectory: "/home/raymond/.ssh"));

        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*does not allow a task to run in*");
    }

    [Fact]
    public async Task WithNoSessionsOpen_OnlyTheProfilesOwnDirectoriesAreAllowed()
    {
        var service = _ServiceWith(
            workspaces: [],
            _Target("qwen", policy => policy with { AllowedWorkingDirs = ["/home/raymond/RiderProjects"] }));

        var task = await service.DelegateAsync(
            new DelegationRequest("qwen", "work", WorkingDirectory: "/home/raymond/RiderProjects/Eveworkbench"));

        task.Status.Should().NotBe(DelegatedTaskStatus.Failed);
    }

    private static SessionProfile _Target(string label, Func<DelegationPolicy, DelegationPolicy>? tune = null)
    {
        var policy = new DelegationPolicy(AllowedAsTarget: true);

        return new SessionProfile(label, ConfigDir: string.Empty, Delegation: tune?.Invoke(policy) ?? policy);
    }

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
