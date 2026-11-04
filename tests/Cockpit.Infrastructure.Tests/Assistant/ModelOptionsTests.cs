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

    private void _Given(ModelIndices indices, PluginModelIntelligence? reading, IReadOnlyList<ModelSnapshot>? snapshots = null)
    {
        _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("Prov", new PluginProviderConfig("p", "{}"))]);
        _public.GetSnapshotsAsync(Arg.Any<CancellationToken>()).Returns(
            snapshots ?? [new ModelSnapshot("m1", "M1", new ModelPrice(5m, 10m), 200_000, true, indices, [], [], "public feed", Now, TimeSpan.FromHours(24))]);
        _provider.ReadAsync("Prov", Arg.Any<CancellationToken>()).Returns(reading);
    }

    [Theory]
    [MemberData(nameof(ModelReferenceCases))]
    public async Task ModelOptions_AssistantToolRefusesAgents_OrchestratorToolShowsOnlyDelegatableProfiles(string model, string snapshotId, string? aliasTarget, string publicModelRef, string? unmatched)
    {
        McpRequestContext.Set("pane-ordinary");

        var result = JsonNode.Parse(await _Tools().ModelOptionsAsync())!;

        Assert.False((bool)result["ok"]!);
        Assert.Contains("not available to an agent session", (string)result["error"]!);
        await _public.DidNotReceive().GetSnapshotsAsync(Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        var snapshots = unmatched is null
            ? new[]
            {
                new ModelSnapshot(snapshotId, "M1", new ModelPrice(5m, 10m), 200_000, true, new ModelIndices(null, 80m, null), [], [], "public feed", Now, TimeSpan.FromHours(24)) { AliasTarget = aliasTarget },
                new ModelSnapshot("example/intelligence-only", "M2", new ModelPrice(5m, 10m), 200_000, true, new ModelIndices(90m, null, null), [], [], "public feed", Now, TimeSpan.FromHours(24)),
            }
            : new[]
            {
                new ModelSnapshot("qwen/qwen3.8-27b", "Closest", new ModelPrice(5m, 10m), 200_000, true, new ModelIndices(null, 80m, null), [], [], "public feed", Now, TimeSpan.FromHours(24)),
                new ModelSnapshot("example/intelligence-only", "M2", new ModelPrice(5m, 10m), 200_000, true, new ModelIndices(90m, null, null), [], [], "public feed", Now, TimeSpan.FromHours(24)),
            };
        _Given(new ModelIndices(null, 80m, null), new PluginModelIntelligence
        {
            Models = [new PluginModelCostEstimate(model), new PluginModelCostEstimate("intelligence-only")],
        }, snapshots);
        _profiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new SessionProfile("Prov", new PluginProviderConfig("p", "{}")),
            new SessionProfile("Other", new PluginProviderConfig("other", "{}")),
        ]);
        var delegation = Substitute.For<IDelegationService>();
        delegation.ListTargetsAsync(Arg.Any<CancellationToken>()).Returns(
            [new DelegationTargetView("Prov", "p", null, [], [], 1, 0, [])]);
        var tools = new OrchestratorTools(delegation, new ModelPictureReader(_public, _provider, _profiles, new FakeClock(Now)));

        var options = JsonNode.Parse(await tools.ModelOptionsAsync(taskType: "coding"))!;

        Assert.True((bool)options["ok"]!);
        Assert.Equal(2, (int)options["count"]!);
        Assert.Equal(unmatched is null ? "coding" : "intelligence", (string)options["ranking"]!["kind"]!);
        Assert.Contains(unmatched is null ? "not comparable" : "Requested coding", (string)options["ranking"]!["message"]!);
        var models = options["models"]!.AsArray();
        var sourceModel = models.Single(candidate => (string)candidate!["model"]! == model)!;
        Assert.Equal(publicModelRef, (string)sourceModel["publicModelRef"]!);
        Assert.Equal(unmatched is null, (bool)sourceModel["comparable"]!);
        if (unmatched is not null)
        {
            Assert.Equal($"unknown: {unmatched}", (string)sourceModel["quality"]!);
        }
        var tool = typeof(OrchestratorTools).GetMethod(nameof(OrchestratorTools.ModelOptionsAsync))!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("model_options", tool.Name);
        Assert.True(tool.ReadOnly);
    }

    public static TheoryData<string, string, string?, string, string?> ModelReferenceCases => new()
    {
        { "claude-opus-5-5", "anthropic/claude-opus-5.5", null, "anthropic/claude-opus-5.5", null },
        { "anthropic/claude-opus-5.5", "anthropic/claude-opus-5.5", null, "anthropic/claude-opus-5.5", null },
        { "~anthropic/claude-opus-latest", "~anthropic/claude-opus-latest", "anthropic/claude-opus-5.5", "anthropic/claude-opus-5.5 via ~anthropic/claude-opus-latest", null },
        { "anthropic/claude-opus-5.5:batch", "anthropic/claude-opus-5.5", null, "anthropic/claude-opus-5.5", null },
        { "gpt-6.1-sol", "openai/gpt-6.1-sol", null, "openai/gpt-6.1-sol", null },
        { "openai/gpt-6.1-sol", "openai/gpt-6.1-sol", null, "openai/gpt-6.1-sol", null },
        { "qwen3.8-27b-Q4_K_M", "qwen/qwen3.8-27b", null, "qwen/qwen3.8-27b", null },
        { "agentic-only", "example/agentic-only", null, "example/agentic-only", null },
        { "onbekend-model", "", null, "onbekend-model", "no match for onbekend-model; closest approximation: qwen/qwen3.8-27b" },
    };

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
