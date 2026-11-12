using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using NSubstitute;
using System.Runtime.CompilerServices;

namespace Cockpit.Plugin.OpenRouterProvider.Tests;

// AC-806: `OpenAiCompatPluginSessionDriver` against a fake `IChatClient`.
// A session that gets no host toolset stays chat-only; the tool loop it runs when it does get one is covered
// by OpenAiCompatPluginSessionDriverToolLoopTests in the Gemini provider's tests (AC-964, one shared driver).
public class OpenAiCompatPluginSessionDriverTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendUserMessage_ReportsAnIdleStreamOrCompletesASteadyOne(bool stalls)
    {
        var chatClient = Substitute.For<IChatClient>();
        var requests = new List<ChatMessage[]>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                requests.Add(call.ArgAt<IEnumerable<ChatMessage>>(0).ToArray());
                return stalls ? _StalledStream(call.ArgAt<CancellationToken>(2)) : _SlowSteadyStream();
            });
        // A stall never sends again, so a short timeout is exact; a steady stream gets 200x its 10 ms gap, so a busy
        // machine cannot stretch one gap past the timeout and turn the steady case into an idle one.
        var idleTimeout = stalls ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(2);
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "anthropic/claude-sonnet-4.5", streamIdleTimeout: idleTimeout);

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        var transcript = string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text));
        Assert.Equal(stalls ? 2 : 1, requests.Count);
        Assert.All(requests, request => Assert.Equal("hi", Assert.Single(request.Where(message => message.Role == ChatRole.User)).Text));
        Assert.Equal(stalls, Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
        if (stalls)
        {
            Assert.Contains("retrying once", transcript);
            Assert.Contains("Raise the stream silence timeout", Assert.Single(events.OfType<PluginSessionError>()).Message);
            Assert.Equal(requests[0], requests[1]);
        }

        else
        {
            Assert.Equal("Hello world.", transcript);
        }

        Assert.Single(events, evt => evt is PluginSessionInitialized);
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
