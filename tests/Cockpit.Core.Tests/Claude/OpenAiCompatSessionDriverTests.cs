using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Claude;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="OpenAiCompatSessionDriver"/> against a fake <see cref="IChatClient"/>: a streamed reply
/// surfaces as ordered <see cref="AssistantTextDelta"/> events followed by a successful
/// <see cref="TurnCompleted"/>, and the driver advertises chat-only capabilities (no tools yet).
/// </summary>
public class OpenAiCompatSessionDriverTests
{
    private static readonly ClaudeProfile LocalProfile =
        new("local", ConfigDir: "", ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1"));

    [Fact]
    public async Task SendUserMessage_StreamsAssistantDeltas_ThenCompletesTheTurn()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream("Hello ", "world."));
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)).Should().Be("Hello world.");
        events.OfType<TurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
        events.Should().ContainSingle(evt => evt is SessionInitialized);
    }

    [Fact]
    public async Task StartAsync_SetsSessionId_AndAdvertisesChatOnlyCapabilities()
    {
        var driver = _CreateDriver(Substitute.For<IChatClient>());

        await driver.StartAsync(LocalProfile);

        driver.SessionId.Should().NotBeNullOrEmpty();
        driver.Capabilities.SupportsTools.Should().BeFalse();
        driver.Capabilities.SupportsPermissions.Should().BeFalse();
    }

    [Fact]
    public async Task SendUserMessage_WhenTheChatClientThrows_EmitsSessionErrorAndAFailedTurn()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Throwing());
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.Should().ContainSingle(evt => evt is SessionError);
        events.OfType<TurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WithASystemPrompt_SendsItAsTheFirstMessage()
    {
        var chatClient = Substitute.For<IChatClient>();
        List<ChatMessage>? captured = null;
        chatClient.GetStreamingResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured = messages.ToList()), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream("ok"));
        var driver = _CreateDriver(chatClient);
        var profile = new ClaudeProfile("local", string.Empty,
            ProviderConfig: new OllamaConfig("http://localhost:11434", "llama3.1", "You are a pirate."));

        await driver.StartAsync(profile);
        await driver.SendUserMessageAsync("hi");
        await _CollectUntilTurnCompletedAsync(driver);

        captured.Should().NotBeNull();
        captured![0].Role.Should().Be(ChatRole.System);
        captured[0].Text.Should().Be("You are a pirate.");
    }

    [Fact]
    public async Task ToolApproval_EmitsToolUseAndPermissionRequested_AndRespondCompletesTheDecision()
    {
        var driver = _CreateDriver(Substitute.For<IChatClient>());
        await driver.StartAsync(LocalProfile);
        var gate = (IToolApprovalGate)driver;

        var approval = gate.RequestApprovalAsync("tool_1", "read_file", """{"path":"x"}""", CancellationToken.None);
        var events = await _CollectUntilAsync(driver, evt => evt is PermissionRequested);

        events.OfType<PermissionRequested>().Should().ContainSingle().Which.ToolName.Should().Be("read_file");
        events.Should().Contain(evt => evt is ToolUseRequested);

        await driver.RespondToPermissionAsync("tool_1", allow: true);
        (await approval).Should().BeTrue();
    }

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns(Array.Empty<AIFunction>());
        toolSession.ConnectedServerNames.Returns(Array.Empty<string>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<CancellationToken>()).Returns(toolSession);

        return new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Throwing()
    {
        await Task.CompletedTask;
        throw new HttpRequestException("server unreachable");
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
    }

    private static Task<List<ClaudeSessionEvent>> _CollectUntilTurnCompletedAsync(ISessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is TurnCompleted);

    private static async Task<List<ClaudeSessionEvent>> _CollectUntilAsync(ISessionDriver driver, Func<ClaudeSessionEvent, bool> until)
    {
        var events = new List<ClaudeSessionEvent>();
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
