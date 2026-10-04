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

    // AC-1473: while another edit of the profile list holds the lock (the admin API's PATCH, say), adding a profile
    // waits for it and then adds to what that edit saved, instead of saving its own older copy over it.
    [Fact]
    public async Task AddLocalModelProfile_WaitsForAnEditInProgress_SoNeitherSavesOverTheOther()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        await ProfileEdits.Gate.WaitAsync();
        Task adding;
        try
        {
            adding = service.AddLocalModelProfileAsync("qwen", provider: "ollama", model: "qwen3:8b", baseUrl: null, purpose: null, tags: null);
            await store.SaveAsync([new SessionProfile("edited by an admin", new OllamaConfig("http://localhost:11434", "llama3"))]);
        }
        finally
        {
            ProfileEdits.Gate.Release();
        }

        await adding;

        Assert.Equal(["edited by an admin", "qwen"], store.Profiles.Select(profile => profile.Label));
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
