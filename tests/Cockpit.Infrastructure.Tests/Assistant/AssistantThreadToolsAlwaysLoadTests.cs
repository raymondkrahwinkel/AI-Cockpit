using System.Reflection;
using Cockpit.Infrastructure.Assistant;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Tests.Assistant;

// Assistant threads: the assistant skipped set_thread when Claude Code deferred it behind tool search, so the thread
// tools and the question card are listed with anthropic/alwaysLoad — read the way the endpoint's WithTools reads them.
public class AssistantThreadToolsAlwaysLoadTests
{
    [Theory]
    [InlineData("set_thread")]
    [InlineData("tag_prompt")]
    [InlineData("ask_structured_question")]
    public void TheThreadToolsAndTheQuestionCard_AreNeverDeferred(string toolName)
    {
        var method = typeof(AssistantAgentMcpTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);

        var tool = McpServerTool.Create(method, _ => null!);

        Assert.True(tool.ProtocolTool.Meta?["anthropic/alwaysLoad"]?.GetValue<bool>());
    }
}
