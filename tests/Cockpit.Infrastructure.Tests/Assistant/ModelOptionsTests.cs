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

    [Fact]
    public async Task ModelOptions_ProviderPrice_WinsOverThePublicOne_AndNamesItsSource()
    {
        var provider = new PluginModelCostEstimate("m1")
        {
            EstimatedInputUsdPerMillionTokens = 1m,
            EstimatedOutputUsdPerMillionTokens = 2m,
            Source = new PluginFactSource("provider live read", Now.AddSeconds(-30), TimeSpan.FromMinutes(5)),
        };
        _Given(new ModelIndices(50m, null, null), new PluginModelIntelligence { Models = [provider] });
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var result = JsonNode.Parse(await _Tools().ModelOptionsAsync())!;

        var price = result["models"]![0]!["price"]!;
        Assert.Equal(1m, (decimal)price["inputPerMTokens"]!);
        Assert.Equal("provider live read", (string)price["fact"]!["source"]!);
        Assert.Equal(30, (int)price["fact"]!["ageSeconds"]!);
        Assert.Contains("provider live read", result["attribution"]!.AsArray().Select(source => (string)source!));
    }

    [Fact]
    public async Task ModelOptions_MissingQuotaAndIndex_AreUnknown_NeverZero()
    {
        _Given(new ModelIndices(50m, null, null), new PluginModelIntelligence { Models = [new PluginModelCostEstimate("m1")] });
        McpRequestContext.Set(AssistantIdentity.PaneId);

        var model = JsonNode.Parse(await _Tools().ModelOptionsAsync("coding"))!["models"]![0]!;

        Assert.Equal("unknown", (string)model["quality"]!);
        Assert.Equal("unknown", (string)model["quota"]!);

        // AC-1813: the assistant is told to consult the tool where it chooses, and never to invent a figure.
        var startAgent = typeof(AssistantAgentMcpTools).GetMethod(nameof(AssistantAgentMcpTools.StartAgentAsync))!.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("model_options", startAgent);
        Assert.Contains("model_options", AssistantSystemPrompt.Default);
        Assert.Contains("never invent a figure", AssistantSystemPrompt.Default);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
