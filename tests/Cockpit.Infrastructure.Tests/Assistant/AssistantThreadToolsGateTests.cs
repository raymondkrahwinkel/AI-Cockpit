using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

// Assistant threads: close_thread goes through the operator like the other assistant gates, under its own label — but
// a close that would be refused anyway (General, something still waiting) is refused before anyone is asked.
public sealed class AssistantThreadToolsGateTests : IDisposable
{
    private readonly IAssistantAgentGateway _gateway = Substitute.For<IAssistantAgentGateway>();

    private readonly IConsentBroker _consent = Substitute.For<IConsentBroker>();

    public AssistantThreadToolsGateTests()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        _gateway.CloseThreadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AssistantThreadResult(true, null));
    }

    public void Dispose() => McpRequestContext.Set(null);

    private AssistantAgentMcpTools _Tools() => new(_gateway, Substitute.For<IAssistantMemory>(), _consent);

    private void _OperatorAnswers(ConsentOutcome outcome) =>
        _consent.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(outcome, false));

    [Fact]
    public async Task AThreadThatCannotClose_IsRefused_BeforeTheOperatorIsAsked()
    {
        _gateway.WhyThreadCannotCloseAsync("depot", Arg.Any<CancellationToken>()).Returns("A question is still waiting in this thread.");

        var result = JsonNode.Parse(await _Tools().CloseThreadAsync("depot"))!;

        Assert.False((bool)result["ok"]!);
        await _consent.DidNotReceiveWithAnyArgs().RequestConsentAsync(default!, default);
        await _gateway.DidNotReceiveWithAnyArgs().CloseThreadAsync(default!, default);
    }

    [Fact]
    public async Task ADeniedClose_ClosesNothing()
    {
        _gateway.WhyThreadCannotCloseAsync("depot", Arg.Any<CancellationToken>()).Returns((string?)null);
        _OperatorAnswers(ConsentOutcome.Denied);

        var result = JsonNode.Parse(await _Tools().CloseThreadAsync("depot"))!;

        Assert.False((bool)result["ok"]!);
        await _gateway.DidNotReceiveWithAnyArgs().CloseThreadAsync(default!, default);
    }

    [Fact]
    public async Task AnApprovedClose_ClosesTheThread_UnderItsOwnLabel()
    {
        _gateway.WhyThreadCannotCloseAsync("depot", Arg.Any<CancellationToken>()).Returns((string?)null);
        _OperatorAnswers(ConsentOutcome.Approved);

        var result = JsonNode.Parse(await _Tools().CloseThreadAsync("depot"))!;

        Assert.True((bool)result["ok"]!);
        await _gateway.Received(1).CloseThreadAsync("depot", Arg.Any<CancellationToken>());
        await _consent.Received(1).RequestConsentAsync(
            Arg.Is<ConsentRequest>(request => request.Source.Label == ConsentSourceCatalog.AssistantThreadClose), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReopeningAThread_AsksNobody()
    {
        _gateway.ReopenThreadAsync("depot", Arg.Any<CancellationToken>()).Returns(new AssistantThreadResult(true, null));

        var result = JsonNode.Parse(await _Tools().ReopenThreadAsync("depot"))!;

        Assert.True((bool)result["ok"]!);
        await _consent.DidNotReceiveWithAnyArgs().RequestConsentAsync(default!, default);
    }
}
