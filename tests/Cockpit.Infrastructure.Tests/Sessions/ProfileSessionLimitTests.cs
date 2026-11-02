using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Tests.BackendApi;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-2026: the profile's parallel-session maximum, enforced where a start registers and shared with delegation.
public sealed class ProfileSessionLimitTests
{
    private static readonly SessionProfile Limited = new("work", new ClaudeConfig(string.Empty), Delegation: new DelegationPolicy(AllowedAsTarget: true, MaxConcurrent: 2))
    {
        MaxParallelSessions = 1,
    };

    private sealed record Rig(SessionLauncher Launcher, SessionRegistry Registry, ProfileSessionLimit Limit, string WorkspaceId)
    {
        public Task<LaunchedSession?> StartAsync(string? paneId = null) =>
            Launcher.StartSessionAsync(new SessionLaunchRequest(WorkspaceId, Limited, null, null, null, PaneSessionKind.Sdk, null, null, null, false, PaneId: paneId));
    }

    private static async Task<Rig> _RigAsync()
    {
        var registry = new SessionRegistry();
        var limit = new ProfileSessionLimit(registry);
        var desks = new InProcBackendContractTests.MemoryDesks();
        var drivers = Substitute.For<ISessionDriverFactory>();
        drivers.Create(Arg.Any<SessionProfile?>()).Returns(_ => new ContractDriver());
        var launcher = new SessionLauncher(
            desks,
            new BackendSessionHosting(new SessionControlFactory(new SessionManager(drivers))),
            registry,
            new SessionStartComposer(Substitute.For<IProjectStore>(), Substitute.For<IMcpServerCatalog>()),
            limit: limit);
        return new Rig(launcher, registry, limit, (await desks.CreateSessionsWorkspaceAsync("Sessions")).Id);
    }

    private static async IAsyncEnumerable<SessionEvent> _NeverEnding()
    {
        await Task.Delay(Timeout.Infinite);
        yield break;
    }

    private static async Task<int> _SucceededAsync(params Func<Task>[] attempts)
    {
        var outcomes = await Task.WhenAll(attempts.Select(attempt => Task.Run(async () =>
        {
            try
            {
                await attempt();
                return true;
            }
            catch (Exception exception) when (exception is ProfileSessionLimitException or DelegationRejectedException)
            {
                return false;
            }
        })));
        return outcomes.Count(admitted => admitted);
    }

    [Fact]
    public async Task TwoSimultaneousStartsOnAProfileAtMaxOne_LetExactlyOneIn_AndARestoredPaneKeepsWaiting()
    {
        var rig = await _RigAsync();
        var awaiting = true;
        var restored = Substitute.For<IHostedSession>();
        restored.PaneId.Returns("restored");
        restored.Title.Returns("restored");
        restored.ActiveProfileLabel.Returns("work");
        restored.AwaitsStart.Returns(_ => awaiting);
        rig.Registry.Register(restored);

        Assert.Equal(1, await _SucceededAsync(() => rig.StartAsync(), () => rig.StartAsync()));
        Assert.Equal(2, rig.Registry.All.Count);

        // AC-2026 B2: the restored pane is not counted while it waits, and now that the profile is full it stays waiting.
        var refusal = await Assert.ThrowsAsync<ProfileSessionLimitException>(() => rig.StartAsync("restored"));
        Assert.Contains("maximum of 1", refusal.Message);
        Assert.True(awaiting);
        await restored.DidNotReceive().StartAsync(Arg.Any<SessionLaunchRequest>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task AStartAndADelegationAtMaxOne_LetExactlyOneIn()
    {
        foreach (var delegationFirst in new[] { false, true })
        {
            await _RaceAsync(delegationFirst);
        }
    }

    private static async Task _RaceAsync(bool delegationFirst)
    {
        var rig = await _RigAsync();
        var profiles = Substitute.For<ISessionProfileStore>();
        profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([Limited]);
        var servers = Substitute.For<IMcpServerStore>();
        servers.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        var working = Substitute.For<ISessionDriver>();
        working.Events.Returns(_NeverEnding());
        var drivers = Substitute.For<ISessionDriverFactory>();
        drivers.Create(Arg.Any<SessionProfile?>()).Returns(working);
        var delegation = new DelegationService(
            profiles, new SessionManager(drivers), servers, Substitute.For<IDelegationAuditLog>(), NoSessionWorkspaces.Instance, limit: rig.Limit);

        // The second round gives the delegation a head start, so the start has to see the task and not only the other way round.
        Assert.Equal(1, await _SucceededAsync(
            async () =>
            {
                await Task.Delay(delegationFirst ? 200 : 0);
                await rig.StartAsync();
            },
            () => delegation.DelegateAsync(new DelegationRequest("work", "go"))));
    }
}
