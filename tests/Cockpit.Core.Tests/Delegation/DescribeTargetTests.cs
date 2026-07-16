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
/// What a calling agent may write back about a profile (#67): what it is good for, and nothing else. An orchestrator
/// learns by using a profile — that this model reviews a diff well and loses the thread on architecture is knowledge
/// the run produced, and knowledge nobody wrote down is knowledge the next session does not have.
/// <para>
/// The line it may not cross is the whole point: everything that decides what a delegated session can <em>do</em>
/// stays with the operator. A caller that could enrol itself as a target, raise a permission ceiling or open a
/// directory would turn every guard in this engine into a suggestion — so these tests are as much about what does not
/// change as about what does.
/// </para>
/// </summary>
public class DescribeTargetTests
{
    [Fact]
    public async Task DescribeTarget_RecordsWhatTheProfileIsGoodFor()
    {
        var store = _Store(_Target("qwen"));
        var service = _Service(store);

        var target = await service.DescribeTargetAsync(
            "qwen",
            purpose: "frontend review — fast, local, weak on architecture",
            tags: ["code", "local"],
            taskTypes: ["review"]);

        target.Purpose.Should().Be("frontend review — fast, local, weak on architecture");
        target.Tags.Should().Equal("code", "local");
        target.AllowedTaskTypes.Should().Equal("review");

        var saved = _Saved(store).Single();
        saved.DelegationPolicy.Purpose.Should().Be("frontend review — fast, local, weak on architecture");
    }

    // The three fields a caller may set are the three fields it sets: everything that governs what a delegated session
    // may do comes back from disk exactly as the operator left it.
    [Fact]
    public async Task DescribeTarget_ChangesNothingThatGovernsWhatATaskMayDo()
    {
        var store = _Store(_Target("qwen", policy => policy with
        {
            MaxConcurrent = 2,
            PermissionCeiling = "acceptEdits",
            AllowedWorkingDirs = ["/home/raymond/RiderProjects"],
            MayDelegateFurther = false,
            TimeoutMinutes = 15,
        }));
        var service = _Service(store);

        await service.DescribeTargetAsync("qwen", "anything at all", tags: ["dangerous"], taskTypes: []);

        var policy = _Saved(store).Single().DelegationPolicy;
        policy.AllowedAsTarget.Should().BeTrue();
        policy.MaxConcurrent.Should().Be(2);
        policy.PermissionCeiling.Should().Be("acceptEdits");
        policy.AllowedWorkingDirs.Should().Equal("/home/raymond/RiderProjects");
        policy.MayDelegateFurther.Should().BeFalse();
        policy.TimeoutMinutes.Should().Be(15);
    }

    // Enrolling a profile as a delegation target is the operator's call. A caller that could do it could make itself
    // one that may do anything, anywhere.
    [Fact]
    public async Task DescribeTarget_OnAProfileThatIsNotATarget_IsRefused()
    {
        var service = _Service(_Store(new SessionProfile("personal", new ClaudeConfig(string.Empty))));

        var describe = async () => await service.DescribeTargetAsync("personal", "let me in", tags: null, taskTypes: null);

        await describe.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*not a delegation target*");
    }

    [Fact]
    public async Task DescribeTarget_LeavesOutAFieldItWasNotGiven()
    {
        var store = _Store(_Target("qwen", policy => policy with { Purpose = "coding", AllowedTaskTypes = ["review"] }));
        var service = _Service(store);

        await service.DescribeTargetAsync("qwen", purpose: null, tags: ["local"], taskTypes: null);

        var policy = _Saved(store).Single().DelegationPolicy;
        policy.Purpose.Should().Be("coding");
        policy.AllowedTaskTypes.Should().Equal("review");
        policy.Tags.Should().Equal("local");
    }

    [Fact]
    public async Task DescribeTarget_ForAnUnknownProfile_IsRefused()
    {
        var service = _Service(_Store(_Target("qwen")));

        var describe = async () => await service.DescribeTargetAsync("nope", "x", tags: null, taskTypes: null);

        await describe.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*No profile named*");
    }

    private static SessionProfile _Target(string label, Func<DelegationPolicy, DelegationPolicy>? tune = null)
    {
        var policy = new DelegationPolicy(AllowedAsTarget: true);

        return new SessionProfile(label, new ClaudeConfig(string.Empty), Delegation: tune?.Invoke(policy) ?? policy);
    }

    private static ISessionProfileStore _Store(params SessionProfile[] profiles)
    {
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);

        return store;
    }

    // What the service actually persisted — the only honest answer to "did it change anything it should not have".
    private static IReadOnlyList<SessionProfile> _Saved(ISessionProfileStore store) =>
        (IReadOnlyList<SessionProfile>)store.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(ISessionProfileStore.SaveAsync))
            .GetArguments()[0]!;

    private static DelegationService _Service(ISessionProfileStore profileStore)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var mcpServerStore = Substitute.For<IMcpServerStore>();
        mcpServerStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);

        return new DelegationService(
            profileStore,
            new SessionManager(driverFactory),
            mcpServerStore,
            Substitute.For<IDelegationAuditLog>(),
            NoSessionWorkspaces.Instance);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
