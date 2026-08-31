using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Assistant;

public sealed class AssistantClearConversationToolTests : IDisposable
{
    [Fact]
    public async Task ClearConversation_ReportsQueuedAndAlreadyQueuedAsSuccessful()
    {
        McpRequestContext.Set(AssistantIdentity.PaneId);
        var gateway = Substitute.For<IAssistantAgentGateway>();
        gateway.RequestConversationClearAsync(Arg.Any<CancellationToken>()).Returns(
            ClearConversationResult.Queued(alreadyPending: false),
            ClearConversationResult.Queued(alreadyPending: true));
        var tools = new AssistantAgentMcpTools(gateway, Substitute.For<IAssistantMemory>());

        var first = JsonNode.Parse(await tools.ClearConversationAsync())!;
        var second = JsonNode.Parse(await tools.ClearConversationAsync())!;

        Assert.True((bool)first["ok"]!);
        Assert.Equal("The conversation will be cleared as soon as this turn finishes.", (string)first["note"]!);
        Assert.True((bool)second["ok"]!);
        Assert.Equal("A conversation clear is already queued for when this turn finishes.", (string)second["note"]!);

        var description = typeof(AssistantAgentMcpTools)
            .GetMethod(nameof(AssistantAgentMcpTools.ClearConversationAsync))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("ANYTHING SAID AFTER THIS CALL", description, StringComparison.Ordinal);
        Assert.Contains("MEMORY AND CURRENT-STATE NOTE ARE NOT CLEARED", description, StringComparison.Ordinal);
        Assert.Contains("call note_state first", description, StringComparison.Ordinal);
    }

    public void Dispose() => McpRequestContext.Set(null);
}
