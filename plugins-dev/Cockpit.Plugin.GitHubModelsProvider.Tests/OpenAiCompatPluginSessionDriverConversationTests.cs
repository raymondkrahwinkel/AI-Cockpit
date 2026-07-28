using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.GitHubModelsProvider.Tests;

/// <summary>
/// <see cref="OpenAiCompatPluginSessionDriver.Conversation"/> (AC-408): this driver accepts a resume target on
/// its <c>StartAsync</c> overload but ignores it — it keeps its own in-memory history rather than a server-side
/// conversation — so it must report <see cref="PluginConversationId.Unsupported"/> rather than the interface's
/// default <c>Known(SessionId)</c>, which would wrongly imply a resumable conversation.
/// </summary>
public class OpenAiCompatPluginSessionDriverConversationTests
{
    [Fact]
    public async Task Conversation_IsUnsupported_EvenAfterASessionIdIsAssigned()
    {
        var driver = new OpenAiCompatPluginSessionDriver(Substitute.For<IChatClient>(), "openai/gpt-4.1");

        await driver.StartAsync();

        Assert.NotNull(driver.SessionId);
        Assert.Equal(PluginConversationId.Unsupported, driver.Conversation);
    }
}
