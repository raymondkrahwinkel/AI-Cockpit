using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using NSubstitute;

namespace Cockpit.Plugin.GitHubModelsProvider.Tests;

// `OpenAiCompatPluginSessionDriver` against a fake `IChatClient` (#63, mirroring
// the Gemini/OpenAI provider plugin's #45 `OpenAiCompatPluginSessionDriverTests`) — same
// history/streaming/error-handling shape.
// A session that gets no host toolset stays chat-only; the tool loop it runs when it does get one is covered
// by OpenAiCompatPluginSessionDriverToolLoopTests in the Gemini provider's tests (AC-964, one shared driver).
public class OpenAiCompatPluginSessionDriverTests
{
    [Fact]
    public async Task SendUserMessage_StreamsAssistantDeltas_ThenCompletesTheTurn()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream("Hello ", "world."));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "openai/gpt-4.1");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Equal("Hello world.", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
        Assert.Single(events, evt => evt is PluginSessionInitialized);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static Task<List<PluginSessionEvent>> _CollectUntilTurnCompletedAsync(IPluginSessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is PluginTurnCompleted);

    private static async Task<List<PluginSessionEvent>> _CollectUntilAsync(IPluginSessionDriver driver, Func<PluginSessionEvent, bool> until)
    {
        var events = new List<PluginSessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (until(evt))
            {
                break;
            }
        }

        return events;
    }
}
