using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Consent;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Plugins;

/// <summary>
/// AC-1724, layer 2 (authorisation): a plugin's prompt into a session goes through the operator's click and nothing
/// else. Run with every bypass switched on and from inside a verified MCP request, the two conditions under which
/// any other source would skip the card, so a pass here cannot come from a bypass.
/// </summary>
public sealed class PluginPromptConsentTests : IDisposable
{
    private const string TargetPane = "pane-worker";

    [Fact]
    public async Task APluginPrompt_IsAlwaysPutToTheOperator_AndOnlyReachesTheSessionAfterSend()
    {
        var bypassEverything = Substitute.For<IConsentBypassPolicy>();
        bypassEverything.ShouldBypass(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>()).Returns(true);
        var broker = new ConsentService(Substitute.For<IConsentAuditLog>(), bypassEverything);
        var cards = new List<ConsentRequest>();
        var answer = ConsentOutcome.Denied;
        broker.PromptOpened += (_, prompt) =>
        {
            cards.Add(prompt.Request);
            broker.Respond(prompt.Id, answer, remember: true);
        };

        var session = Substitute.For<ISessionHandle>();
        session.Title.Returns("worker");
        var registry = Substitute.For<ISessionRegistry>();
        registry.Find(TargetPane).Returns(session);
        var gateway = Substitute.For<IAssistantAgentGateway>();
        gateway.SendPromptAsync(TargetPane, "run the tests", Arg.Any<CancellationToken>())
            .Returns(AgentPromptResult.Handed(TargetPane, "worker", delivered: true));
        var services = new ServiceCollection()
            .AddSingleton<IConsentBroker>(broker)
            .AddSingleton(registry)
            .AddSingleton(gateway)
            .BuildServiceProvider();
        var actions = new PluginScopedActions("time-tracking", "Time tracking", Substitute.For<ICockpitActions>(), services);
        McpRequestContext.Set("pane-caller");

        var declined = await Assert.ThrowsAsync<InvalidOperationException>(() => actions.SendPromptToSessionAsync(TargetPane, "run the tests", "book today"));
        await gateway.DidNotReceive().SendPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        answer = ConsentOutcome.Approved;
        await actions.SendPromptToSessionAsync(TargetPane, "run the tests", "book today");

        Assert.Equal("Declined by the operator.", declined.Message);
        await gateway.Received(1).SendPromptAsync(TargetPane, "run the tests", Arg.Any<CancellationToken>());
        Assert.Equal(2, cards.Count);
        Assert.All(cards, card => Assert.Equal(("time-tracking", ConsentRisk.Dangerous), (card.Source.PluginId, card.Risk)));
        Assert.Contains("run the tests", cards[1].Action, StringComparison.Ordinal);
        Assert.Contains("book today", cards[1].Action, StringComparison.Ordinal);
    }

    public void Dispose() => McpRequestContext.Set(null);
}
