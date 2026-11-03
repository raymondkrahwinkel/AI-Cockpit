using System.Reflection;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.ModelIntelligence;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using ModelContextProtocol.Server;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

// AC-1813: model_options covers the assistant tool and the orchestrator's delegation rights.
public sealed class ModelOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly IModelIntelligence _public = Substitute.For<IModelIntelligence>();

    private readonly IProviderModelIntelligence _provider = Substitute.For<IProviderModelIntelligence>();

    private readonly ISessionProfileStore _profiles = Substitute.For<ISessionProfileStore>();

    internal static ModelPictureReader PictureReader() =>
        new(Substitute.For<IModelIntelligence>(), Substitute.For<IProviderModelIntelligence>(), Substitute.For<ISessionProfileStore>());

    private AssistantReadMcpTools _Tools() =>
        new(
            Substitute.For<IAssistantReadGateway>(),
            Substitute.For<IDelegationService>(),
            Substitute.For<INodeSessionsClient>(),
            new NodeDiscoveryId(Path.Combine(Path.GetTempPath(), $"node-discovery-id-{Guid.NewGuid():N}.txt")),
            new ModelPictureReader(_public, _provider, _profiles, new FakeClock(Now)));

    private void _Given(ModelIndices indices, PluginModelIntelligence? reading)
    {
        _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Prov", new PluginProviderConfig("p", "{}"))]);
        _public.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(
            [new ModelSnapshot("m1", "M1", new ModelPrice(5m, 10m), 200_000, true, indices, [], [], "public feed", Now, TimeSpan.FromHours(24))]);
        _provider.ReadAsync("Prov", Arg.Any<CancellationToken>()).Returns(reading);
    }

    [Fact]
    public async Task ModelOptions_AssistantToolRefusesAgents_OrchestratorToolShowsOnlyDelegatableProfiles()
    {
        McpRequestContext.Set("pane-ordinary");

        var result = JsonNode.Parse(await _Tools().ModelOptionsAsync())!;

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);
        await _public.DidNotReceive().GetSnapshotsAsync(Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        _Given(new ModelIndices(50m, null, null), new PluginModelIntelligence
        {
            Models = [new PluginModelCostEstimate("m1")],
        });
        _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SessionProfile("Prov", new PluginProviderConfig("p", "{}")),
            new SessionProfile("Other", new PluginProviderConfig("other", "{}")),
        ]);
        var delegation = Substitute.For<IDelegationService>();
        delegation.ListTargetsAsync(Arg.Any<CancellationToken>()).Returns(
            [new DelegationTargetView("Prov", "p", null, [], [], 1, 0, [])]);
        var tools = new OrchestratorTools(delegation, new ModelPictureReader(_public, _provider, _profiles, new FakeClock(Now)));

        var options = JsonNode.Parse(await tools.ModelOptionsAsync())!;

        Assert.True((bool)options["ok"]!);
        Assert.Equal(1, (int)options["count"]!);
        Assert.Equal("Prov", (string)Assert.Single(options["models"]!.AsArray())!["profile"]!);
        var tool = typeof(OrchestratorTools).GetMethod(nameof(OrchestratorTools.ModelOptionsAsync))!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("model_options", tool.Name);
        Assert.True(tool.ReadOnly);
    }

    // AC-1855 merged two cases: the provider's price wins with its source, and a missing quota or index is unknown, never 0.

    public static TheoryData<string, string, string, string, Func<JsonNode, string>> ModelReferenceCases => new()
    {
        { "claude-opus-5-5", "coding", "anthropic/claude-opus-5.5", "coding missing → intelligence", _Basis },
        { "anthropic/claude-opus-5.5", "coding", "anthropic/claude-opus-5.5", "coding missing → intelligence", _Basis },
        { "~anthropic/claude-opus-latest", "coding", "anthropic/claude-opus-5.5 via ~anthropic/claude-opus-latest", "coding missing → intelligence", _Basis },
        { "anthropic/claude-opus-5.5:batch", "coding", "anthropic/claude-opus-5.5", "coding missing → intelligence", _Basis },
        { "gpt-6.1-sol", "coding", "openai/gpt-6.1-sol", "coding missing → intelligence", _Basis },
        { "openai/gpt-6.1-sol", "coding", "openai/gpt-6.1-sol", "coding missing → intelligence", _Basis },
        { "qwen3.8-27b-Q4_K_M", "coding", "qwen/qwen3.8-27b", "coding missing → intelligence", _Basis },
        { "agentic-only", "agentic", "example/agentic-only", "agentic missing → coding", _Basis },
        { "onbekend-model", "coding", "onbekend-model", "unknown: no match for onbekend-model; closest approximation: qwen/qwen3.8-27b", _Quality },
    };


    private static string _Basis(JsonNode row) => row["quality"]?["basis"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing fallback basis");

    private static string _Quality(JsonNode row) => row["quality"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing quality");

    private sealed class FeedHandler(string feed) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(feed) });
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
