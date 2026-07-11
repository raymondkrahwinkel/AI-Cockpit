using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Cockpit.Plugin.GitHubModelsProvider;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.GitHubModelsProvider.Tests;

/// <summary>
/// <see cref="OpenAiCompatPluginSessionDriver"/> against a fake <see cref="IChatClient"/> (#63, mirroring
/// the Gemini/OpenAI provider plugin's #45 <c>OpenAiCompatPluginSessionDriverTests</c>) — same
/// history/streaming/error-handling shape, minus the tool-loop (this driver has no tool source of its own,
/// so <see cref="PluginSessionCapabilities.SupportsTools"/> is always false).
/// </summary>
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

        string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)).Should().Be("Hello world.");
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
        events.Should().ContainSingle(evt => evt is PluginSessionInitialized);
    }

    [Fact]
    public async Task StartAsync_SetsSessionId_AndAdvertisesChatOnlyCapabilities()
    {
        var driver = new OpenAiCompatPluginSessionDriver(Substitute.For<IChatClient>(), "openai/gpt-4.1");

        await driver.StartAsync();

        driver.SessionId.Should().NotBeNullOrEmpty();
        driver.Capabilities.SupportsTools.Should().BeFalse();
        driver.Capabilities.SupportsPermissions.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithAModelOverride_UsesItForTheTurnInsteadOfTheDefault()
    {
        var chatClient = Substitute.For<IChatClient>();
        ChatOptions? captured = null;
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Do<ChatOptions>(options => captured = options), Arg.Any<CancellationToken>())
            .Returns(_Stream("ok"));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "openai/gpt-4.1");

        await driver.StartAsync(model: "meta/llama-3.3-70b-instruct");
        await driver.SendUserMessageAsync("hi");
        await _CollectUntilTurnCompletedAsync(driver);

        captured.Should().NotBeNull();
        captured!.ModelId.Should().Be("meta/llama-3.3-70b-instruct");
    }

    [Fact]
    public async Task SendUserMessage_WhenTheChatClientThrows_EmitsSessionErrorAndAFailedTurn()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Throwing());
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "openai/gpt-4.1");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.Should().ContainSingle(evt => evt is PluginSessionError);
        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task InterruptAsync_CancelsTheInFlightTurn_AndReportsItAsInterrupted()
    {
        var chatClient = Substitute.For<IChatClient>();
        // The fake stream observes the real per-turn CancellationToken the driver passes through, so
        // cancelling it (via InterruptAsync) is what actually ends the turn — not a race on timing.
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _StreamThatWaitsForCancellation((CancellationToken)callInfo[2]));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "openai/gpt-4.1");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        await driver.InterruptAsync();
        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.OfType<PluginTurnCompleted>().Should().ContainSingle().Which.StopReason.Should().Be("interrupt");
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _StreamThatWaitsForCancellation([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Throwing()
    {
        await Task.CompletedTask;
        throw new HttpRequestException("server unreachable");
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
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
