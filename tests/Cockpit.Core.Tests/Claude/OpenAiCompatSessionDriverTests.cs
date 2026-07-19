using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
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
        // SendUserMessageAsync ignores the images parameter entirely (#64) — advertising vision support
        // here would be the exact dead promise the capability model exists to prevent.
        driver.Capabilities.SupportsVision.Should().BeFalse();
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
        var profile = new SessionProfile(
            "local",
            new OllamaConfig("http://localhost:11434", "llama3.1", "You are a pirate."));

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
        (await approval).Approved.Should().BeTrue();
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

        events.OfType<PermissionRequested>().Should().BeEmpty();
        events.OfType<ToolResult>().Should().ContainSingle().Which.IsError.Should().BeFalse();
        events.OfType<ToolResult>().Single().Content.Should().Contain("echoed:hi");
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

        events.OfType<PermissionRequested>().Should().BeEmpty();
        var result = events.OfType<ToolResult>().Should().ContainSingle().Subject;
        result.IsError.Should().BeTrue();
        result.Content.Should().NotContain("echoed:hi", "the denied tool must not actually run");
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

        deniedEvents.OfType<PermissionRequested>().Should().BeEmpty();
        deniedEvents.OfType<ToolResult>().Should().ContainSingle().Which.IsError.Should().BeTrue();

        // ...but runs when the operator listed it, even under the most restrictive ceiling.
        var allowedClient = Substitute.For<IChatClient>();
        allowedClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var allowed = _CreateDriver(allowedClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = ToolPermissionClass.Unknown }, AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo"));
        await allowed.StartAsync(LocalProfile);
        await allowed.SetDelegatedToolGateAsync("plan", ["echo"]);
        await allowed.SendUserMessageAsync("go");
        var allowedEvents = await _CollectUntilTurnCompletedAsync(allowed);

        allowedEvents.OfType<PermissionRequested>().Should().BeEmpty();
        allowedEvents.OfType<ToolResult>().Should().ContainSingle().Which.Content.Should().Contain("echoed:hi");
    }

    [Fact]
    public async Task LocalToolCall_SurfacesToolUseAndResult_ThroughTheFunctionInvocationLoop()
    {
        // The model asks to call "echo" on its first streamed response, then (after the tool result is fed
        // back) answers with plain text — the exact shape UseFunctionInvocation drives for a local model.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        driver.Capabilities.SupportsTools.Should().BeTrue();
        await driver.SendUserMessageAsync("use the tool");

        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (evt is PermissionRequested permission)
            {
                await driver.RespondToPermissionAsync(permission.ToolUseId, allow: true);
            }

            if (evt is TurnCompleted)
            {
                break;
            }
        }

        // The tool call and its result surface as their own events, so the UI can render tool rows for a
        // local model exactly as it does for Claude.
        events.OfType<ToolUseRequested>().Should().ContainSingle().Which.ToolName.Should().Be("echo");
        events.OfType<ToolResult>().Should().ContainSingle().Which.Content.Should().Contain("echoed:hi");
        string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)).Should().Be("done");
    }

    [Fact]
    public async Task AutoApproveTools_RunsAToolCallWithoutAPermissionPrompt()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetAutoApproveToolsAsync(true);
        await driver.SendUserMessageAsync("use the tool");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        // The tool still surfaces, but no approval was requested — the "allow all tools" convenience.
        events.OfType<ToolUseRequested>().Should().ContainSingle();
        events.OfType<ToolResult>().Should().ContainSingle();
        events.OfType<PermissionRequested>().Should().BeEmpty();
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

        events.OfType<PermissionRequested>().Should().BeEmpty();
        var result = events.OfType<ToolResult>().Should().ContainSingle().Subject;
        result.IsError.Should().BeTrue();
        result.Content.Should().NotContain("echoed:hi");
    }

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, params AIFunction[] tools) =>
        _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass>(), tools);

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, IReadOnlyDictionary<string, ToolPermissionClass> toolClasses, params AIFunction[] tools)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns(tools);
        toolSession.ConnectedServerNames.Returns(tools.Length == 0 ? Array.Empty<string>() : new[] { "test-server" });
        toolSession.ToolClasses.Returns(toolClasses);
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(toolSession);

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

    private static async IAsyncEnumerable<ChatResponseUpdate> _Throwing()
    {
        await Task.CompletedTask;
        throw new HttpRequestException("server unreachable");
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
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
