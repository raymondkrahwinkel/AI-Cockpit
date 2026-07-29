using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
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

        Assert.Equal(SessionProvider.Ollama.ToString(), created.Provider);
        Assert.Equal("qwen2.5-coder:7b", created.Model);
        Assert.Equal("http://localhost:11434", created.BaseUrl);
        Assert.Equal("cheap local coding", created.Purpose);
        Assert.Equal(new[] { "code", "local" }, created.Tags);

        var saved = store.Profiles.Single(profile => profile.Label == "qwen-coder");
        var ollamaConfig = Assert.IsType<OllamaConfig>(saved.ProviderConfig);
        Assert.Equivalent(new { BaseUrl = "http://localhost:11434", Model = "qwen2.5-coder:7b" }, ollamaConfig);
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
        Assert.False(store.Profiles.Single().DelegationPolicy.AllowedAsTarget);
        Assert.Empty(await service.ListTargetsAsync());

        // ...and delegating to it is refused for exactly that reason, until the operator turns it on.
        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("qwen", "do a thing"));
        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("not available as a delegation target", thrown.Message);
    }

    [Fact]
    public async Task AddLocalModelProfile_DefaultsTheLmStudioBaseUrl_WhenOmitted()
    {
        var store = new InMemoryProfileStore();
        var service = _Service(store);

        var created = await service.AddLocalModelProfileAsync(
            "lm", provider: "lmstudio", model: "some-model", baseUrl: null, purpose: null, tags: null);

        Assert.Equal("http://localhost:1234", created.BaseUrl);
        Assert.IsType<LmStudioConfig>(store.Profiles.Single().ProviderConfig);
    }

    [Fact]
    public async Task AddLocalModelProfile_ForADuplicateLabel_IsRefused()
    {
        var store = new InMemoryProfileStore(new SessionProfile("qwen", new OllamaConfig("http://localhost:11434", "qwen")));
        var service = _Service(store);

        var add = async () => await service.AddLocalModelProfileAsync(
            "QWEN", provider: "ollama", model: "m", baseUrl: null, purpose: null, tags: null);

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(add);
        Assert.Contains("already exists", thrown.Message);
    }

    [Fact]
    public async Task AddLocalModelProfile_ForANonLocalProvider_IsRefused()
    {
        var service = _Service(new InMemoryProfileStore());

        var add = async () => await service.AddLocalModelProfileAsync(
            "sneaky", provider: "some-cloud-agent", model: "big-model", baseUrl: null, purpose: null, tags: null);

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(add);
        Assert.Contains("not a local model provider", thrown.Message);
    }

    [Fact]
    public async Task AddLocalModelProfile_WithoutAModel_IsRefused()
    {
        var service = _Service(new InMemoryProfileStore());

        var add = async () => await service.AddLocalModelProfileAsync(
            "qwen", provider: "ollama", model: "   ", baseUrl: null, purpose: null, tags: null);

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(add);
        Assert.Contains("model id", thrown.Message);
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

    [Fact]
    public void ListProviders_ReturnsTheLocalScaffoldableProviders_AndTheRegisteredPluginProviders()
    {
        var registry = new PluginProviderRegistry();
        registry.Register(new SessionProviderRegistration(
            ProviderId: "sample-agent",
            DisplayName: "Sample Agent",
            CreateDriverFactory: _ => Substitute.For<IPluginSessionDriverFactory>(),
            Capabilities: new PluginSessionCapabilities(true, true),
            CreateConfigView: _ => Substitute.For<IPluginProviderConfigView>()));

        var service = new DelegationService(
            new InMemoryProfileStore(),
            new SessionManager(Substitute.For<ISessionDriverFactory>()),
            Substitute.For<IMcpServerStore>(),
            Substitute.For<IDelegationAuditLog>(),
            NoSessionWorkspaces.Instance,
            registry);

        var providers = service.ListProviders();

        // The two local providers are the caller's to scaffold with add_profile; the plugin provider is the
        // operator's to create (it carries a login), so it is listed but not addable this way.
        var ollama = Assert.Single(providers, p => p.Name == "ollama");
        Assert.Equivalent(new { DisplayName = "Ollama", Kind = "local", AddableWithAddProfile = true }, ollama);
        var lmstudio = Assert.Single(providers, p => p.Name == "lmstudio");
        Assert.Equivalent(new { DisplayName = "LM Studio", Kind = "local", AddableWithAddProfile = true }, lmstudio);
        var sampleAgent = Assert.Single(providers, p => p.Name == "sample-agent");
        Assert.Equivalent(new { DisplayName = "Sample Agent", Kind = "plugin", AddableWithAddProfile = false }, sampleAgent);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
