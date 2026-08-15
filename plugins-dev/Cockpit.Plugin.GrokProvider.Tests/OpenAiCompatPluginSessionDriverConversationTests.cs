using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.GrokProvider.Tests;

// AC-408: this driver keeps its own in-memory history, not a server-side conversation, so it must report
// Unsupported rather than the interface's default Known(SessionId), which would imply a real resume.
public class OpenAiCompatPluginSessionDriverConversationTests
{
    [Fact]
    public async Task Conversation_IsUnsupported_EvenAfterASessionIdIsAssigned()
    {
        var driver = new OpenAiCompatPluginSessionDriver(Substitute.For<IChatClient>(), "grok-4.6");

        await driver.StartAsync();

        Assert.NotNull(driver.SessionId);
        Assert.Equal(PluginConversationId.Unsupported, driver.Conversation);
    }
}
