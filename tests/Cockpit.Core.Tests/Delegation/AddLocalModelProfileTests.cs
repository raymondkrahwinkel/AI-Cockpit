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
/// Scaffolding a local-model profile through the orchestrator (#67, AC-6): a caller can add an Ollama or LM Studio
/// model so it is ready to use, without editing the profiles file by hand. The line it must not cross is the same
/// one <see cref="DescribeTargetTests"/> guards — a caller cannot make what it adds a delegation target, because
/// what a delegated session may do is the operator's to set. So the load-bearing test here is that a freshly added
/// profile is <em>not</em> a target.
/// </summary>
public class AddLocalModelProfileTests
{
    [Fact]
    public async Task AddLocalModelProfile_AddsAnOllamaProfile_CarryingItsModelAndSuggestedPurpose()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        var created = await service.AddLocalModelProfileAsync(
            "qwen-coder", provider: "ollama", model: "qwen2.5-coder:7b",
            baseUrl: null, purpose: "cheap local coding", tags: ["code", "local"]);

        created.Provider.Should().Be(SessionProvider.Ollama.ToString());
        created.Model.Should().Be("qwen2.5-coder:7b");
        created.BaseUrl.Should().Be("http://localhost:11434");
        created.Purpose.Should().Be("cheap local coding");
        created.Tags.Should().Equal("code", "local");

        var saved = store.Profiles.Single(profile => profile.Label == "qwen-coder");
        saved.ProviderConfig.Should().BeOfType<OllamaConfig>()
            .Which.Should().BeEquivalentTo(new { BaseUrl = "http://localhost:11434", Model = "qwen2.5-coder:7b" });
    }

    [Fact]
    public async Task AddLocalModelProfile_IsNeverADelegationTarget_SoAddingItGrantsNoDelegationRights()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        await service.AddLocalModelProfileAsync(
            "qwen", provider: "ollama", model: "qwen3:8b",
            baseUrl: null, purpose: "review", tags: ["review"]);

        // The whole point: a caller can add a local model, but not enrol it as something it may delegate to.
        store.Profiles.Single().DelegationPolicy.AllowedAsTarget.Should().BeFalse();
        (await service.ListTargetsAsync()).Should().BeEmpty("adding a profile must not make it a delegation target");

        // ...and delegating to it is refused for exactly that reason, until the operator turns it on.
        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("qwen", "do a thing"));
        await delegate_.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*not available as a delegation target*");
    }

    [Fact]
    public async Task AddLocalModelProfile_DefaultsTheLmStudioBaseUrl_WhenOmitted()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        var created = await service.AddLocalModelProfileAsync(
            "lm", provider: "lmstudio", model: "some-model", baseUrl: null, purpose: null, tags: null);

        created.BaseUrl.Should().Be("http://localhost:1234");
        store.Profiles.Single().ProviderConfig.Should().BeOfType<LmStudioConfig>();
    }

    [Fact]
    public async Task AddLocalModelProfile_ForADuplicateLabel_IsRefused()
    {
        var store = new InMemoryProfileStore(new SessionProfile("qwen", ConfigDir: string.Empty));
        var service = _Service(store);

        var add = async () => await service.AddLocalModelProfileAsync(
            "QWEN", provider: "ollama", model: "m", baseUrl: null, purpose: null, tags: null);

        await add.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*already exists*");
    }

    [Fact]
    public async Task AddLocalModelProfile_ForANonLocalProvider_IsRefused()
    {
        var service = _Service(new InMemoryProfileStore());

        var add = async () => await service.AddLocalModelProfileAsync(
            "sneaky", provider: "claude", model: "opus", baseUrl: null, purpose: null, tags: null);

        await add.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*not a local model provider*");
    }

    [Fact]
    public async Task AddLocalModelProfile_WithoutAModel_IsRefused()
    {
        var service = _Service(new InMemoryProfileStore());

        var add = async () => await service.AddLocalModelProfileAsync(
            "qwen", provider: "ollama", model: "   ", baseUrl: null, purpose: null, tags: null);

        await add.Should().ThrowAsync<DelegationRejectedException>().WithMessage("*model id*");
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
