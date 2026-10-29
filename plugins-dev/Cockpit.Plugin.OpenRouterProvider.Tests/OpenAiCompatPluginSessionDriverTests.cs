using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using NSubstitute;
using System.Runtime.CompilerServices;

namespace Cockpit.Plugin.OpenRouterProvider.Tests;

// AC-806: `OpenAiCompatPluginSessionDriver` against a fake `IChatClient`, mirroring the sibling
// GitHub Models plugin's own driver tests — same shape.
// A session that gets no host toolset stays chat-only; the tool loop it runs when it does get one is covered
// by OpenAiCompatPluginSessionDriverToolLoopTests in the Gemini provider's tests (AC-964, one shared driver).
public class OpenAiCompatPluginSessionDriverTests
{
    [Fact]
    public async Task SendUserMessage_ReportsAnIdleStreamOrCompletesASteadyOne()
    {
        foreach (var stalls in new[] { true, false })
        {
            var chatClient = Substitute.For<IChatClient>();
            chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
                .Returns(stalls ? _StalledStream() : _SlowSteadyStream());
            var driver = new OpenAiCompatPluginSessionDriver(chatClient, "anthropic/claude-sonnet-4.5", streamIdleTimeout: TimeSpan.FromMilliseconds(50));

            await driver.StartAsync();
            await driver.SendUserMessageAsync("hi");
            var events = await _CollectUntilTurnCompletedAsync(driver);

            Assert.Equal(stalls ? "Hello" : "Hello world.", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
            Assert.Equal(stalls, Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
            if (stalls)
            {
                Assert.Contains("did not send an update", Assert.Single(events.OfType<PluginSessionError>()).Message);
            }

            Assert.Single(events, evt => evt is PluginSessionInitialized);
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _StalledStream([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello ");
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _SlowSteadyStream([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in new[] { "Hello ", "world." })
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
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
