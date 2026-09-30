using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Mcp;
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
    private static readonly SessionProfile LocalProfile =
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

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

        Assert.Equal("Hello world.", string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<TurnCompleted>()).IsError);
        Assert.Single(events, evt => evt is SessionInitialized);
    }

    [Fact]
    public async Task ToolApproval_EmitsToolUseAndPermissionRequested_AndRespondCompletesTheDecision()
    {
        // Driven through a real tool call rather than the gate interface: the gate moved to its own type
        // (AC-964, shared with the plugin-provider loop), so the seam worth testing is what a turn produces.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("read_file", ("path", "x")), _Stream("done"));
        var driver = _CreateDriver(chatClient, AIFunctionFactory.Create((string path) => $"read {path}", "read_file"));
        await driver.StartAsync(LocalProfile);

        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilAsync(driver, evt => evt is PermissionRequested);

        var prompt = Assert.Single(events.OfType<PermissionRequested>());
        Assert.Equal("read_file", prompt.ToolName);
        Assert.Contains(events, evt => evt is ToolUseRequested);

        await driver.RespondToPermissionAsync(prompt.ToolUseId, allow: true);
        var completed = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Contains("read x", completed.OfType<ToolResult>().Single().Content);
    }

    [Fact]
    public async Task DelegatedGate_RunsAToolWithinTheCeiling_WithoutAPrompt()
    {
        // AC-79: a delegated session decides tool calls against the ceiling — a read-only tool runs under any
        // ceiling, non-interactively, with no PermissionRequested.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.ReadOnly }, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("plan", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        Assert.False(Assert.Single(events.OfType<ToolResult>()).IsError);
        Assert.Contains("echoed:hi", events.OfType<ToolResult>().Single().Content);
    }

    [Fact]
    public async Task DelegatedGate_DeniesAToolAboveTheCeiling_WithReasonAndNoPrompt()
    {
        // A destructive tool is denied under acceptEdits: no prompt (nobody to answer), and the denial is fed back
        // as the tool result so the model can adapt rather than hang.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Destructive }, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("acceptEdits", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.DoesNotContain("echoed:hi", result.Content);
    }

    [Fact]
    public async Task DelegatedGate_DeniesAnUnknownTool_UnlessOnTheAllowList()
    {
        // An unclassifiable tool is denied even at bypassPermissions when not allow-listed...
        var deniedClient = Substitute.For<IChatClient>();
        deniedClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var denied = _CreateDriver(deniedClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown }, AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo"));
        await denied.StartAsync(LocalProfile);
        await denied.SetDelegatedToolGateAsync("bypassPermissions", []);
        await denied.SendUserMessageAsync("go");
        var deniedEvents = await _CollectUntilTurnCompletedAsync(denied);

        Assert.Empty(deniedEvents.OfType<PermissionRequested>());
        Assert.True(Assert.Single(deniedEvents.OfType<ToolResult>()).IsError);

        // ...but runs when the operator listed it, even under the most restrictive ceiling.
        var allowedClient = Substitute.For<IChatClient>();
        allowedClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var allowed = _CreateDriver(allowedClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown }, AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo"));
        await allowed.StartAsync(LocalProfile);
        await allowed.SetDelegatedToolGateAsync("plan", ["echo"]);
        await allowed.SendUserMessageAsync("go");
        var allowedEvents = await _CollectUntilTurnCompletedAsync(allowed);

        Assert.Empty(allowedEvents.OfType<PermissionRequested>());
        Assert.Contains("echoed:hi", Assert.Single(allowedEvents.OfType<ToolResult>()).Content);
    }

    [Fact]
    public async Task DelegatedGate_ToolMissingFromTheClassMap_IsTreatedAsUnknownAndDenied()
    {
        // Fail-safe: a tool the classification map has no entry for defaults to Unknown → denied, even at the most
        // permissive ceiling, with no prompt. Guards against a regression that dropped the explicit Unknown fallback.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        // Empty class map — "echo" is absent.
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass>(), echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync("bypassPermissions", []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.DoesNotContain("echoed:hi", result.Content);
    }

    public static IEnumerable<object[]> RepliesWithNothingToShow() =>
    [
        [Array.Empty<string>()],
        [new[] { "   ", "\n" }],
    ];

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, params AIFunction[] tools) =>
        _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass>(), tools);

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses, params AIFunction[] tools)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([.. tools.Select(tool => new McpSessionTool(tool, "test-server", AlwaysMounted: false))]);
        toolSession.ConnectedServerNames.Returns(tools.Length == 0 ? Array.Empty<string>() : new[] { "test-server" });
        toolSession.ToolClasses.Returns(toolClasses);
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(toolSession);

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

    private static async IAsyncEnumerable<ChatResponseUpdate> _ToolCall(string name, params (string Key, object? Value)[] args)
    {
        var arguments = args.ToDictionary(pair => pair.Key, pair => pair.Value);
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent($"call_{name}", name, arguments)],
        };

        await Task.CompletedTask;
    }

    private static Task<List<SessionEvent>> _CollectUntilTurnCompletedAsync(ISessionDriver driver) =>
        _CollectUntilAsync(driver, evt => evt is TurnCompleted);

    private static async Task<List<SessionEvent>> _CollectUntilAsync(ISessionDriver driver, Func<SessionEvent, bool> until)
    {
        var events = new List<SessionEvent>();
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
