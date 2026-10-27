using System.Reflection;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.ModelIntelligence;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using ModelContextProtocol.Server;
using NSubstitute;
using System.ComponentModel;

namespace Cockpit.Infrastructure.Tests.Assistant;

// AC-1813: model_options states the model picture for the assistant, and only for the assistant.
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

    // Security: the model picture belongs to the assistant; an ordinary session is refused before anything is read.
    [Fact]
    public async Task ModelOptions_FromAnOrdinaryAgentSession_IsRefused_AndNothingIsRead()
    {
        McpRequestContext.Set("pane-ordinary");

        var result = JsonNode.Parse(await _Tools().ModelOptionsAsync())!;

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);
        await _public.DidNotReceive().GetSnapshotsAsync(Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // AC-1855 merged two cases: the provider's price wins with its source, and a missing quota or index is unknown, never 0.
    [Fact]
    public async Task ModelOptions_ProviderPriceWins_AndMissingQuotaAndIndex_AreUnknown_NeverZero()
    {
        var provider = new PluginModelCostEstimate("m1")
        {
            EstimatedInputUsdPerMillionTokens = 1m,
            EstimatedOutputUsdPerMillionTokens = 2m,
            Source = new PluginFactSource("provider live read", Now.AddSeconds(-30), TimeSpan.FromMinutes(5)),
        };
        _Given(new ModelIndices(50m, null, null), new PluginModelIntelligence { Models = [provider] });
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = JsonNode.Parse(await _Tools().ModelOptionsAsync("coding"))!;
        var model = result["models"]![0]!;

        var price = model["price"]!;
        Assert.Equal(1m, (decimal)price["inputPerMTokens"]!);
        Assert.Equal("provider live read", (string)price["fact"]!["source"]!);
        Assert.Equal(30, (int)price["fact"]!["ageSeconds"]!);
        Assert.Contains("provider live read", result["attribution"]!.AsArray().Select(source => (string)source!));

        Assert.StartsWith("unknown: OpenRouter carries no Artificial Analysis coding index", (string)model["quality"]!);
        Assert.Equal("unknown", (string)model["quota"]!);

        // AC-1813/AC-1855: the step stands in BEFORE A SPAWN, the tool in WHAT EXISTS, and it is preloaded, not deferred.
        var startAgent = typeof(AssistantAgentMcpTools).GetMethod(nameof(AssistantAgentMcpTools.StartAgentAsync))!.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("model_options", startAgent);
        var prompt = AssistantSystemPrompt.Default;
        var start = prompt.IndexOf("BEFORE A SPAWN, CHECK THESE", StringComparison.Ordinal);
        var beforeSpawn = prompt[start..prompt.IndexOf("IN EVERY BRIEF, WHATEVER", start, StringComparison.Ordinal)];
        Assert.Contains("a fixed step, unasked: before every `start_agent`, and before you give a subagent a model, call `model_options`", beforeSpawn);
        Assert.Contains("never invent a figure", beforeSpawn);
        Assert.Contains("- Model choice (AC-1855): `model_options`", prompt[prompt.IndexOf("WHAT EXISTS", StringComparison.Ordinal)..]);
        var meta = typeof(AssistantReadMcpTools).GetMethod(nameof(AssistantReadMcpTools.ModelOptionsAsync))!.GetCustomAttributes<McpMetaAttribute>();
        Assert.Contains(meta, attribute => attribute.Name == AssistantAgentMcpTools.AlwaysLoadMeta);
    }

    [Fact]
    public async Task ModelOptions_JoinsProviderRefsToOpenRouterIds_WithPriceSourceAndIndex()
    {
        // The real feed's shape: versioned canonical_slug, alias_target as an object, indices as numbers.
        const string feed = """
            {"data":[
              {"id":"anthropic/claude-opus-5.5","canonical_slug":"anthropic/claude-opus-5.5-20260921","name":"Opus","context_length":1000000,
               "pricing":{"prompt":"0.000004","completion":"0.00002"},"benchmarks":{"artificial_analysis":{"intelligence_index":57.6,"coding_index":null}}},
              {"id":"~anthropic/claude-opus-latest","canonical_slug":"~anthropic/claude-opus-latest","alias_target":{"slug":"anthropic/claude-opus-5.5"},"pricing":{"prompt":"0.000004","completion":"0.00002"}},
              {"id":"openai/gpt-5.6-terra","canonical_slug":"openai/gpt-5.6-terra-20260709","pricing":{"prompt":"0.000002","completion":"0.000012"},
               "benchmarks":{"artificial_analysis":{"coding_index":76.7}}}]}
            """;
        var root = Path.Combine(Path.GetTempPath(), $"ac1855-{Guid.NewGuid():N}");
        try
        {
            var publicData = new OpenRouterModelIntelligence(root, new FeedHandler(feed), new FakeClock(Now));
            _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Prov", new PluginProviderConfig("p", "{}"))]);
            _provider.ReadAsync("Prov", Arg.Any<CancellationToken>()).Returns(new PluginModelIntelligence
            {
                Models = [new PluginModelCostEstimate("opus") { PublicModelRef = "~anthropic/claude-opus-latest" }, new PluginModelCostEstimate("gpt-5.6-terra") { PublicModelRef = "gpt-5.6-terra" }],
            });
            McpRequestContext.Set(AssistantIdentity.PaneId);
            var tools = new AssistantReadMcpTools(
                Substitute.For<IAssistantReadGateway>(), Substitute.For<IDelegationService>(), Substitute.For<INodeSessionsClient>(),
                new NodeDiscoveryId(Path.Combine(root, "node-discovery-id.txt")), new ModelPictureReader(publicData, _provider, _profiles, new FakeClock(Now)));

            var models = JsonNode.Parse(await tools.ModelOptionsAsync("coding"))!["models"]!.AsArray();

            var opus = models.Single(row => (string)row!["model"]! == "opus")!;
            Assert.Equal(4m, (decimal)opus["price"]!["inputPerMTokens"]!);
            Assert.Equal("OpenRouter /api/v1/models", (string)opus["price"]!["fact"]!["source"]!);
            Assert.False((bool)opus["price"]!["fact"]!["stale"]!);
            var terra = models.Single(row => (string)row!["model"]! == "gpt-5.6-terra")!;
            Assert.Equal("openai/gpt-5.6-terra", (string)terra["publicModelRef"]!);
            Assert.Equal(12m, (decimal)terra["price"]!["outputPerMTokens"]!);
            Assert.Equal(76.7m, (decimal)terra["quality"]!["index"]!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
