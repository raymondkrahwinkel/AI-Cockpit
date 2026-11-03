using Microsoft.Extensions.AI;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using OpenAI;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// <see cref="OpenAiCompatSessionDriver"/> against a fake <see cref="IChatClient"/>: a streamed reply
/// surfaces as ordered <see cref="AssistantTextDelta"/> events followed by a successful
/// <see cref="TurnCompleted"/>, and the driver advertises chat-only capabilities (no tools yet).
/// </summary>
public class OpenAiCompatSessionDriverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_AreSentInProviderWireFormat_AndRetainedInHistory(bool ollama)
    {
        using var handler = new ImageWireHandler(ollama);
        using var http = new HttpClient(handler);
        var options = OpenAiCompatChatClientFactory.BuildClientOptions("http://localhost:1234");
        options.Transport = new HttpClientPipelineTransport(http);
        using IChatClient client = ollama
            ? new OllamaChatClient(new OllamaConfig("http://localhost:1234", "model"), http)
            : new OpenAIClient(new ApiKeyCredential("test"), options).GetChatClient("model").AsIChatClient();
        var driver = _CreateDriver(client);
        await driver.StartAsync(LocalProfile);
        await driver.SendUserMessageAsync("describe", [new("image/png", "AQID"), new("image/jpeg", "BAUG")]);
        await _CollectUntilTurnCompletedAsync(driver);
        await driver.SendUserMessageAsync("", [new("image/png", "BwgJ")]);
        await _CollectUntilTurnCompletedAsync(driver);

        var messages = handler.Bodies[1].GetProperty("messages");
        var first = messages[0];
        var last = messages[messages.GetArrayLength() - 1];
        if (ollama)
        {
            Assert.Equal("describe", first.GetProperty("content").GetString());
            Assert.Equal("AQID", first.GetProperty("images")[0].GetString());
            Assert.Equal("BAUG", first.GetProperty("images")[1].GetString());
            Assert.Equal("BwgJ", last.GetProperty("images")[0].GetString());
        }
        else
        {
            Assert.Equal("describe", first.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal("image_url", first.GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("image_url", first.GetProperty("content")[2].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID", first.GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/jpeg;base64,BAUG", first.GetProperty("content")[2].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/png;base64,BwgJ", last.GetProperty("content")[0].GetProperty("image_url").GetProperty("url").GetString());
        }
    }

    private sealed class ImageWireHandler(bool ollama) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content ?? throw new InvalidOperationException("Request has no body.");
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await content.ReadAsStringAsync(cancellationToken)));
            var response = ollama
                ? "{\"message\":{\"role\":\"assistant\",\"content\":\"done\"},\"done\":true}\n"
                : "data: {\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, ollama ? "application/json" : "text/event-stream") };
        }
    }
    private static readonly SessionProfile LocalProfile =
        new("local", new OllamaConfig("http://localhost:11434", "llama3.1"));

    [Fact]
    public async Task ToolApproval_EmitsToolUseAndPermissionRequested_AndRespondCompletesTheDecision()
    {
        // Driven through a real tool call rather than the gate interface: the gate moved to its own type
        // (AC-964, shared with the plugin-provider loop), so the seam worth testing is what a turn produces.
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("read_file", ("path", "x")), _Stream("done"));
        var driver = _CreateDriver(chatClient,
            [AIFunctionFactory.Create((string path) => $"read {path}", "read_file"),
                .. Enumerable.Range(0, CockpitToolSearch.PreloadThreshold).Select(index => AIFunctionFactory.Create(() => "unused", $"filler_{index}"))]);
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

    // Never a prompt (nobody answers it): a denial goes back as the tool result. Destructive is denied under acceptEdits,
    // unclassifiable even at bypassPermissions unless allow-listed, and an allow-listed tool runs under any ceiling.
    [Theory]
    [InlineData(ToolPermissionClass.Destructive, "acceptEdits", false)]
    [InlineData(ToolPermissionClass.Unknown, "bypassPermissions", false)]
    [InlineData(ToolPermissionClass.Unknown, "plan", true)]
    public async Task DelegatedGate_DeniesAboveTheCeilingOrUnknown_UnlessAllowListed_WithoutAPrompt(
        ToolPermissionClass toolClass, string ceiling, bool allowListed)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ToolCall("echo", ("text", "hi")), _Stream("done"));
        var echo = AIFunctionFactory.Create((string text) => $"echoed:{text}", "echo");
        var driver = _CreateDriver(chatClient, new Dictionary<string, ToolPermissionClass> { ["echo"] = toolClass }, echo);

        await driver.StartAsync(LocalProfile);
        await driver.SetDelegatedToolGateAsync(ceiling, allowListed ? ["echo"] : []);
        await driver.SendUserMessageAsync("go");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Empty(events.OfType<PermissionRequested>());
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.Equal(!allowListed, result.IsError);
        Assert.Equal(allowListed, result.Content.Contains("echoed:hi", StringComparison.Ordinal));
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
        toolProvider.ConnectAsync(Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>()).Returns(toolSession);

        return new OpenAiCompatSessionDriver(factory, toolProvider, NullLogger<OpenAiCompatSessionDriver>.Instance, Substitute.For<ILocalModelContextManager>());
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
