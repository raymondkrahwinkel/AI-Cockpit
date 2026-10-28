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
              {"id":"openai/gpt-6.1-sol","canonical_slug":"openai/gpt-6.1-sol-20260929","pricing":{"prompt":"0.000002","completion":"0.000012"},
               "benchmarks":{"artificial_analysis":{"intelligence_index":76.7}}},
              {"id":"other/gpt-6.1-sol","pricing":{"prompt":"0.000099"}},
              {"id":"anthropic/claude-sonnet-5.5","canonical_slug":"anthropic/claude-sonnet-5.5-20260928",
               "pricing":{"prompt":"0.000002","completion":"0.00001"},"benchmarks":{"artificial_analysis":{"intelligence_index":56}}},
              {"id":"anthropic/claude-fable-5.1","pricing":{"prompt":"0.00001","completion":"0.00005"},
               "benchmarks":{"artificial_analysis":{"intelligence_index":53.4}}},
              {"id":"~anthropic/claude-fable-latest","alias_target":{"slug":"anthropic/claude-fable-5.1"}}]}
            """;
        var root = Path.Combine(Path.GetTempPath(), $"ac1855-{Guid.NewGuid():N}");
        try
        {
            var publicData = new OpenRouterModelIntelligence(root, new FeedHandler(feed), new FakeClock(Now));
            _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([
                new SessionProfile("Prov", new PluginProviderConfig("claude", "{}")),
                new SessionProfile("Codex", new PluginProviderConfig("cli-agent-provider.codex", "{}"))]);
            _provider.ReadAsync("Prov", Arg.Any<CancellationToken>()).Returns(new PluginModelIntelligence
            {
                Models = [
                    new PluginModelCostEstimate("opus") { PublicModelRef = "~anthropic/claude-opus-latest" },
                    new PluginModelCostEstimate("sonnet") { PublicModelRef = "anthropic/claude-sonnet-5.5" },
                    new PluginModelCostEstimate("fable") { PublicModelRef = "fable" },
                    new PluginModelCostEstimate("missing") { PublicModelRef = "anthropic/missing" }],
            });
            _provider.ReadAsync("Codex", Arg.Any<CancellationToken>()).Returns(new PluginModelIntelligence
            {
                Models = [new PluginModelCostEstimate("gpt-6.1-sol") { PublicModelRef = "gpt-6.1-sol" }],
            });
            McpRequestContext.Set(AssistantIdentity.PaneId);
            var tools = new AssistantReadMcpTools(
                Substitute.For<IAssistantReadGateway>(), Substitute.For<IDelegationService>(), Substitute.For<INodeSessionsClient>(),
                new NodeDiscoveryId(Path.Combine(root, "node-discovery-id.txt")), new ModelPictureReader(publicData, _provider, _profiles, new FakeClock(Now)));

            var models = (JsonNode.Parse(await tools.ModelOptionsAsync()) ?? throw new InvalidOperationException("Missing result"))["models"]?.AsArray()
                ?? throw new InvalidOperationException("Missing models");

            var opus = models.Single(row => (string)row!["model"]! == "opus")!;
            Assert.Equal(4m, (decimal)opus["price"]!["inputPerMTokens"]!);
            Assert.Equal("OpenRouter /api/v1/models", (string)opus["price"]!["fact"]!["source"]!);
            Assert.False((bool)opus["price"]!["fact"]!["stale"]!);
            foreach (var (model, reference, input, quality) in new[]
            {
                ("sonnet", "anthropic/claude-sonnet-5.5", 2m, 56m),
                ("gpt-6.1-sol", "openai/gpt-6.1-sol", 2m, 76.7m),
                ("fable", "~anthropic/claude-fable-latest", 10m, 53.4m),
            })
            {
                var row = models.Single(row => row?["model"]?.GetValue<string>() == model)
                    ?? throw new InvalidOperationException("Missing model");
                Assert.Equal(reference, row["publicModelRef"]?.GetValue<string>());
                Assert.Equal(input, row["price"]?["inputPerMTokens"]?.GetValue<decimal>());
                Assert.Equal(quality, row["quality"]?["index"]?.GetValue<decimal>());
            }

            var missing = models.Single(row => row?["model"]?.GetValue<string>() == "missing")
                ?? throw new InvalidOperationException("Missing unknown model");
            Assert.Equal("unknown: no public match for anthropic/missing", missing["price"]?.GetValue<string>());
            Assert.Equal("unknown: no public match for anthropic/missing", missing["quality"]?.GetValue<string>());
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
