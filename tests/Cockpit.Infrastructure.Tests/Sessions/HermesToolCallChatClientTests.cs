using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-192: local models (qwen-coder via Ollama) emit tool-calls as Hermes/XML text in the content field, which
/// <c>UseFunctionInvocation</c> never sees — so the call is never run and the run hangs. These prove the
/// <see cref="HermesToolCallChatClient"/> shim turns that text into a structured <see cref="FunctionCallContent"/>
/// (name + parameters), leaves plain text and already-structured calls untouched, and reassembles a block split across
/// streamed updates — plus the driver's no-progress net that fails a turn whose text still carries an unconverted
/// marker with no tool run.
/// </summary>
public class HermesToolCallChatClientTests
{
    private static readonly SessionProfile LocalProfile =
        new("local", new OllamaConfig("http://localhost:11434", "qwen2.5-coder"));

    [Fact]
    public async Task ZeroParameterHermesCall_BecomesAFunctionCallWithNoArguments_AndTheMarkersDoNotLeakAsText()
    {
        // The literal shape qwen-coder emits for a no-arg tool: <function=NAME> </function> </tool_call>.
        var (text, calls) = await _RunAsync("<function=list_allowed_directories> </function> </tool_call>");

        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("list_allowed_directories");
        calls[0].Arguments.Should().BeEmpty();
        // Neither the function marker nor the tool_call wrapper survives as text.
        text.Should().NotContain("<function=");
        text.Should().NotContain("</tool_call>");
    }

    [Fact]
    public async Task OneParameterHermesCall_BecomesAFunctionCallWithThatArgument()
    {
        var (_, calls) = await _RunAsync("<function=read_file> <parameter=path> /home/x/README.md </parameter> </function> </tool_call>");

        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("read_file");
        calls[0].Arguments.Should().ContainKey("path").WhoseValue.Should().Be("/home/x/README.md");
    }

    [Fact]
    public async Task MultiParameterHermesCall_CarriesEveryParameter()
    {
        var (_, calls) = await _RunAsync(
            "<function=write_file><parameter=path>/tmp/a.txt</parameter><parameter=content>hello world</parameter></function>");

        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("write_file");
        calls[0].Arguments.Should().ContainKey("path").WhoseValue.Should().Be("/tmp/a.txt");
        calls[0].Arguments.Should().ContainKey("content").WhoseValue.Should().Be("hello world");
    }

    [Fact]
    public async Task PlainText_FlowsThroughUntouched_WithNoFunctionCall()
    {
        var (text, calls) = await _RunAsync("Here is my answer, no tools needed.");

        calls.Should().BeEmpty();
        text.Should().Be("Here is my answer, no tools needed.");
    }

    [Fact]
    public async Task PrecedingText_IsPreserved_ThenTheCallIsSynthesised()
    {
        var (text, calls) = await _RunAsync("Let me look at it. <function=read_file><parameter=path>/x</parameter></function>");

        text.Should().Contain("Let me look at it.");
        calls.Should().ContainSingle().Which.Name.Should().Be("read_file");
    }

    [Fact]
    public async Task AHermesBlockSplitAcrossStreamedUpdates_IsReassembledIntoOneFunctionCall()
    {
        // The stream cuts the block at arbitrary boundaries — mid-marker, mid-parameter — exactly as an HTTP stream
        // does. The shim buffers across updates and still yields one correct call.
        var (_, calls) = await _RunAsync(
            "<functio", "n=read_file> <param", "eter=path> /home/x/READ", "ME.md </parameter> </func", "tion> </tool_call>");

        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("read_file");
        calls[0].Arguments.Should().ContainKey("path").WhoseValue.Should().Be("/home/x/README.md");
    }

    [Fact]
    public async Task AnAlreadyStructuredFunctionCall_IsLeftUntouched()
    {
        // A model that emits a real FunctionCallContent (not Hermes text) must pass through verbatim — the shim only
        // rewrites text, never a call that arrived structured.
        var original = new FunctionCallContent("call_abc123", "echo", new Dictionary<string, object?> { ["text"] = "hi" });
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_StructuredStream(original));
        var client = new HermesToolCallChatClient(inner);

        var (_, calls) = await _CollectAsync(client);

        calls.Should().ContainSingle();
        calls[0].Should().BeSameAs(original);
    }

    [Fact]
    public async Task SendUserMessage_WithAHermesTextToolCall_RunsTheToolThroughTheFunctionInvocationLoop()
    {
        // End-to-end proof of the fix: the model asks to call "echo" as HERMES TEXT (not structured), then — after the
        // tool result is fed back — answers with plain text. The shim + UseFunctionInvocation must run the tool exactly
        // as they would a natively-structured call. Red before the shim: the text landed in the assistant bubble and
        // the turn ended "success" with the tool never run.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                _Stream("<function=echo> <parameter=text> hi </parameter> </function> </tool_call>"),
                _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, echo);

        await driver.StartAsync(LocalProfile);
        driver.Capabilities.SupportsTools.Should().BeTrue();
        await driver.SetAutoApproveToolsAsync(true);
        await driver.SendUserMessageAsync("read something");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.OfType<ToolUseRequested>().Should().ContainSingle().Which.ToolName.Should().Be("echo");
        events.OfType<ToolResult>().Should().ContainSingle().Which.Content.Should().Contain("echoed:hi");
        string.Concat(events.OfType<AssistantTextDelta>().Select(delta => delta.Text)).Should().Be("done");
        events.OfType<TurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task SendUserMessage_EndingWithAnUnprocessedToolCallMarker_AndNoToolActivity_SurfacesAVisibleError()
    {
        // AC-192 no-progress net: an incomplete/malformed Hermes marker the shim could not convert (here an opened
        // <function= that never closes) survives to the assistant text with no tool ever run. That must become a
        // visible error, not the silent "success" a pseudo-tool-call used to end as.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream("Let me read it. <function=read_file"));
        var driver = _CreateDriver(chatClient);

        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        events.OfType<SessionError>().Should().ContainSingle().Which.Message.Should().Contain("tool call");
        events.OfType<TurnCompleted>().Should().ContainSingle().Which.IsError.Should().BeTrue();
    }

    [Fact]
    public void ContainsUnprocessedToolCallMarker_DetectsAnOpenFunctionOrAStrayToolCallWrapper()
    {
        OpenAiCompatSessionDriver._ContainsUnprocessedToolCallMarker("call <function=read_file now").Should().BeTrue();
        OpenAiCompatSessionDriver._ContainsUnprocessedToolCallMarker("done </tool_call>").Should().BeTrue();
        OpenAiCompatSessionDriver._ContainsUnprocessedToolCallMarker("a perfectly ordinary answer").Should().BeFalse();
    }

    private static async Task<(string Text, List<FunctionCallContent> Calls)> _RunAsync(params string[] chunks)
    {
        var inner = Substitute.For<IChatClient>();
        inner.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_Stream(chunks));
        return await _CollectAsync(new HermesToolCallChatClient(inner));
    }

    private static async Task<(string Text, List<FunctionCallContent> Calls)> _CollectAsync(HermesToolCallChatClient client)
    {
        var text = new System.Text.StringBuilder();
        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync([], null, CancellationToken.None))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent textContent:
                        text.Append(textContent.Text);
                        break;
                    case FunctionCallContent call:
                        calls.Add(call);
                        break;
                }
            }
        }

        return (text.ToString(), calls);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _StructuredStream(FunctionCallContent call)
    {
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [call] };
        await Task.CompletedTask;
    }

    private static OpenAiCompatSessionDriver _CreateDriver(IChatClient chatClient, params AIFunction[] tools)
    {
        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(Arg.Any<ProviderConfig>()).Returns(chatClient);

        var toolSession = Substitute.For<IMcpToolSession>();
        toolSession.Tools.Returns(tools);
        toolSession.ConnectedServerNames.Returns(tools.Length == 0 ? Array.Empty<string>() : new[] { "test-server" });
        toolSession.ToolClasses.Returns(new Dictionary<string, ToolPermissionClass>());
        var toolProvider = Substitute.For<IMcpToolProvider>();
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(toolSession);

        return new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance);
    }

    private static async Task<List<SessionEvent>> _CollectUntilTurnCompletedAsync(ISessionDriver driver)
    {
        var events = new List<SessionEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            events.Add(evt);
            if (evt is TurnCompleted)
            {
                break;
            }
        }

        return events;
    }
}
