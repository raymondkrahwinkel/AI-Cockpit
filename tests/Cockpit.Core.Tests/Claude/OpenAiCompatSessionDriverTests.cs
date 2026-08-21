using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
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
    public async Task StartAsync_SetsSessionId_AndAdvertisesChatOnlyCapabilities()
    {
        var driver = _CreateDriver(Substitute.For<IChatClient>());

        await driver.StartAsync(LocalProfile);

        Assert.False(string.IsNullOrEmpty(driver.SessionId));
        Assert.False(driver.Capabilities.SupportsTools);
        Assert.False(driver.Capabilities.SupportsPermissions);
        // SendUserMessageAsync ignores the images parameter entirely (#64) — advertising vision support
        // here would be the exact dead promise the capability model exists to prevent.
        Assert.False(driver.Capabilities.SupportsVision);
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

        Assert.Single(events, evt => evt is SessionError);
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
    }

    [Fact]
    public async Task SendUserMessage_WhenTheSdkThrowsCancelWithoutAUserInterrupt_EmitsSessionErrorNotInterrupted()
    {
        // AC-132: the OpenAI SDK aborts the stream as an OperationCanceledException on some non-2xx responses.
        // With no user interrupt the turn's token is not cancelled, so this must surface as a real error, not a
        // silent "interrupted" (the bug where a 400 made the beurt vanish).
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ThrowingCancel());
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Single(events, evt => evt is SessionError);
        var turn = Assert.Single(events.OfType<TurnCompleted>());
        Assert.True(turn.IsError);
        Assert.NotEqual("interrupted", turn.Subtype);
    }

    [Fact]
    public async Task SendUserMessage_WhenTheUserInterrupts_CompletesAsInterruptedWithoutAnError()
    {
        // The other side of the discrimination: a genuine interrupt (the turn's token cancelled) stays a clean
        // "interrupted" turn with no error row.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => _BlockUntilCancelled(call.Arg<CancellationToken>()));
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");

        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (evt is AssistantTextDelta)
            {
                await driver.InterruptAsync();
            }

            if (evt is TurnCompleted)
            {
                break;
            }
        }

        Assert.DoesNotContain(events, evt => evt is SessionError);
        Assert.Equal("interrupted", Assert.Single(events.OfType<TurnCompleted>()).Subtype);
    }

    [Fact]
    public async Task SendUserMessage_WhenTheStreamIsEmptyWithNoToolCalls_EmitsAVisibleNoResponseError()
    {
        // AC-132 vangnet: a request that ends the stream empty without throwing (a 200 with an empty body, a
        // swallowed error) used to emit a blank success — nothing shown. It must leave a visible notice.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream());
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Contains("no response", Assert.Single(events.OfType<SessionError>()).Message);
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
    }

    [Fact]
    public void DescribeError_WithAPlainException_ReturnsItsMessage()
    {
        var described = OpenAiCompatSessionDriver._DescribeError(new HttpRequestException("server unreachable"));

        Assert.Equal("server unreachable", described);
    }

    [Fact]
    public void DescribeError_WithAClientResultException_AppendsTheResponseBody()
    {
        // The real reason (context exceeded, tool-template failure, …) is in the HTTP body, not the exception's
        // own "HTTP 400" message — _DescribeError must surface it so the operator sees why (AC-132).
        var response = Substitute.For<PipelineResponse>();
        response.Content.Returns(BinaryData.FromString(
            """{"error":{"code":"exceed_context_size_error","message":"request 29078 tokens exceeds 8192"}}"""));
        var error = new ClientResultException(response);

        var described = OpenAiCompatSessionDriver._DescribeError(error);

        Assert.Contains("exceed_context_size_error", described);
        Assert.Contains("29078 tokens exceeds 8192", described);
    }

    [Fact]
    public void DescribeError_WithAHugeResponseBody_TruncatesIt()
    {
        // A misbehaving/hostile server could answer with a huge body; it must not be copied wholesale into the
        // transcript, log and (for a delegated session) the audit log and orchestrator result.
        var response = Substitute.For<PipelineResponse>();
        response.Content.Returns(BinaryData.FromString(new string('x', 100_000)));
        var error = new ClientResultException(response);

        var described = OpenAiCompatSessionDriver._DescribeError(error);

        Assert.Contains("… (truncated)", described);
        Assert.True(described.Length < 5_000);
    }

    [Theory]
    [InlineData("Unable to generate parser for this template. Automatic parser generation failed: Tool call IDs should be alphanumeric strings with length 9!", true)]
    [InlineData("This model does not support tool calling in this runtime", true)]
    [InlineData("tools are not supported by this model", true)]
    [InlineData("tool calling is not supported for this template", true)]
    [InlineData("exceed_context_size_error: request 29078 tokens exceeds 8192", false)]
    [InlineData("HTTP 400 (Bad Request)", false)]
    // A bare tool-call-id complaint from a server that DOES support tools must not be read as "can't do tools".
    [InlineData("Invalid request: tool call id 'abc' must be 9 alphanumeric characters", false)]
    public void IsToolTemplateError_DetectsAToolOrTemplateRejection_NotAnOrdinaryError(string message, bool expected)
    {
        Assert.Equal(expected, OpenAiCompatSessionDriver._IsToolTemplateError(message));
    }

    [Fact]
    public async Task SendUserMessage_WhenTheModelRejectsTools_RetriesWithoutToolsAndAnswers_WithAVisibleNote()
    {
        // AC-135: a local model whose template can't do tool-calling rejects the tools-carrying request; rather
        // than fail, the driver notes it and retries once with no tools so a plain answer still comes back.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ThrowingClientError("""{"error":{"message":"Unable to generate parser for this template. Tool call IDs should be alphanumeric strings with length 9!"}}"""),
                _Stream("Answer without tools."));
        var echo = AIFunctionFactory.Create((string text) => text, "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        Assert.True(driver.Capabilities.SupportsTools);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        // The note rides as assistant text (not a SessionError, which would end the turn mid-retry), so the bubble
        // holds the note and then the tool-less answer; the retry must have carried no tools.
        var text = string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text));
        Assert.Contains("does not support tool-calling", text);
        Assert.Contains("Answer without tools.", text);
        Assert.Empty(events.OfType<SessionError>());
        Assert.False(Assert.Single(events.OfType<TurnCompleted>()).IsError);
        chatClient.Received().GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(), Arg.Is<ChatOptions>(options => options.Tools == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendUserMessage_WhenTheModelRejectsTools_AndTheRetryAlsoFails_SurfacesTheError()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ThrowingClientError("""{"error":{"message":"Unable to generate parser for this template."}}"""),
                _Throwing());
        var echo = AIFunctionFactory.Create((string text) => text, "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        // The note (assistant text) plus the retry's own error — the turn still ends visibly, not silently.
        Assert.Contains("does not support tool-calling", string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)));
        Assert.Single(events.OfType<SessionError>());
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
    }

    [Fact]
    public async Task SendUserMessage_WhenToolsAreRejected_AndTheRetryReturnsNothing_SurfacesTheNoResponseNotice()
    {
        // The retry (no tools) coming back empty must still leave the no-response notice, not a silent success —
        // the flag reset before the retry keeps that check honest.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _ThrowingClientError("""{"error":{"message":"Unable to generate parser for this template."}}"""),
                _Stream());
        var echo = AIFunctionFactory.Create((string text) => text, "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Contains("no response", Assert.Single(events.OfType<SessionError>()).Message);
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
    }

    [Fact]
    public async Task SendUserMessage_WhenAnErrorIsNotAToolTemplateFailure_SurfacesItWithoutRetrying()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ThrowingClientError("""{"error":{"code":"exceed_context_size_error","message":"request 29078 tokens exceeds 8192"}}"""));
        var echo = AIFunctionFactory.Create((string text) => text, "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        var error = Assert.Single(events.OfType<SessionError>());
        Assert.Contains("exceed_context_size_error", error.Message);
        Assert.DoesNotContain("does not support tool-calling", error.Message);
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
        chatClient.Received(1).GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendUserMessage_WhenTheReplyIsWhitespaceOnly_EmitsTheNoResponseNotice()
    {
        // A whitespace-only reply is as empty as no reply — it must hit the no-response net, not show a blank
        // success bubble.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream("   ", "\n"));
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Contains("no response", Assert.Single(events.OfType<SessionError>()).Message);
        Assert.True(Assert.Single(events.OfType<TurnCompleted>()).IsError);
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

        Assert.NotNull(captured);
        Assert.Equal(ChatRole.System, captured![0].Role);
        Assert.Equal("You are a pirate.", captured[0].Text);
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
        Assert.True(driver.Capabilities.SupportsTools);
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
        Assert.Equal("echo", Assert.Single(events.OfType<ToolUseRequested>()).ToolName);
        Assert.Contains("echoed:hi", Assert.Single(events.OfType<ToolResult>()).Content);
        Assert.Equal("done", string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)));
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
        Assert.Single(events.OfType<ToolUseRequested>());
        Assert.Single(events.OfType<ToolResult>());
        Assert.Empty(events.OfType<PermissionRequested>());
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

    [Fact]
    public async Task StartAsync_WithNoPerSessionSelection_ConnectsTheToolLoopWithTheProfilesSavedMcpSelection()
    {
        IReadOnlySet<string>? captured = null;
        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([]);
        toolSession.ConnectedServerNames.Returns(Array.Empty<string>());
        toolSession.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider
            .ConnectAsync(Arg.Do<IReadOnlySet<string>?>(names => captured = names), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(toolSession);
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(Substitute.For<IChatClient>());
        var driver = new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance);
        var profile = new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"))
        {
            EnabledMcpServerNames = ["cockpit-youtrack", "cockpit-session"],
        };

        // #44/AC-130: a local-model session opened programmatically (a plugin/workflow shortcut, a restored session)
        // carries no per-session selection, so the tool loop must connect with the profile's saved checklist rather
        // than every server. Proven red before EffectiveSessionSelection, when ConnectAsync received null.
        await driver.StartAsync(profile);

        Assert.Equivalent(new object[] { "cockpit-youtrack", "cockpit-session" }, captured);
    }

    [Fact]
    public async Task StartAsync_ConnectsTheToolLoopOnThePaneIdTheHostGaveIt()
    {
        string? captured = null;
        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns([]);
        toolSession.ConnectedServerNames.Returns(Array.Empty<string>());
        toolSession.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider
            .ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Do<string?>(pane => captured = pane), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(toolSession);
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(Substitute.For<IChatClient>());
        var driver = new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance);
        var profile = new SessionProfile("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

        // AC-89/AC-106: a local-model session must reach the cockpit's own endpoints on the pane the host stamped it
        // with, exactly as a CLI session does. That id is what a delegated task's worktrees are keyed on, so a driver
        // that dropped it would leave them keyed on whatever the model claimed — and the cleanup on stop would then
        // release nothing. The ceremony is the same for both providers; only this half is provider-specific.
        await driver.StartAsync(profile, launchOptions: new Dictionary<string, string>
        {
            [WellKnownPluginSessionOptions.PaneId] = "task-42",
        });

        Assert.Equal("task-42", captured);
    }

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

    private static async IAsyncEnumerable<ChatResponseUpdate> _Throwing()
    {
        await Task.CompletedTask;
        throw new HttpRequestException("server unreachable");
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
    }

    // A ClientResultException carrying a given response body — the shape the OpenAI SDK throws on a non-2xx, used
    // to exercise the tool-template detection and body surfacing.
    private static async IAsyncEnumerable<ChatResponseUpdate> _ThrowingClientError(string body)
    {
        await Task.CompletedTask;
        var response = Substitute.For<PipelineResponse>();
        response.Content.Returns(BinaryData.FromString(body));
        throw new ClientResultException(response);
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
    }

    // The OpenAI SDK's shape for a stream aborted on a non-2xx: an OperationCanceledException with no user
    // cancellation behind it (the turn's token stays uncancelled).
    private static async IAsyncEnumerable<ChatResponseUpdate> _ThrowingCancel()
    {
        await Task.CompletedTask;
        throw new OperationCanceledException("stream aborted");
#pragma warning disable CS0162 // Unreachable code — the yield makes this an iterator producing the throw.
        yield break;
#pragma warning restore CS0162
    }

    // Streams one delta, then blocks until the turn's token is cancelled and surfaces that as an
    // OperationCanceledException carrying the cancelled token — a real user interrupt mid-stream.
    private static async IAsyncEnumerable<ChatResponseUpdate> _BlockUntilCancelled([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "partial ");
        await Task.Delay(Timeout.Infinite, ct);
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
