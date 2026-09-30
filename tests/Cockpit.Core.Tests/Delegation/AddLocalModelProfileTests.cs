using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Scaffolding a local-model profile through the orchestrator (#67, AC-6): a caller can add an Ollama or LM Studio
/// model so it is ready to use, without editing the profiles file by hand. The line it must not cross is the same
/// one <see cref="DescribeTargetTests"/> guards — a caller cannot make what it adds a delegation target, because
/// what a delegated session may do is the operator's to set. So the load-bearing test here is that a freshly added
/// profile is <em>not</em> a target.
/// </summary>
public class AddLocalModelProfileTests
{
    [Fact]
    public async Task AddLocalModelProfile_IsNeverADelegationTarget_SoAddingItGrantsNoDelegationRights()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        await service.AddLocalModelProfileAsync(
            "qwen", provider: "ollama", model: "qwen3:8b",
            baseUrl: null, purpose: "review", tags: ["review"]);

        // The whole point: a caller can add a local model, but not enrol it as something it may delegate to.
        Assert.False(store.Profiles.Single().DelegationPolicy.AllowedAsTarget);
        Assert.Empty(await service.ListTargetsAsync());

        // ...and delegating to it is refused for exactly that reason, until the operator turns it on.
        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("qwen", "do a thing"));
        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("not available as a delegation target", thrown.Message);
    }

    private sealed class InMemoryProfileStore : ISessionProfileStore
    {
        public InMemoryProfileStore(params SessionProfile[] seed) => Profiles = [.. seed];

        public List<SessionProfile> Profiles { get; private set; }

        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>(Profiles);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default)
        {
            Profiles = [.. profiles];
            return Task.CompletedTask;
        }
    }

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
