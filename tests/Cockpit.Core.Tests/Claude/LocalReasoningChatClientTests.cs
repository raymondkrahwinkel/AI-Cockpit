using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Cockpit.Core.Tests.Claude;

public class LocalReasoningChatClientTests
{
    [Fact]
    public async Task EarlierThinking_IsReplayedOnTheWire_WithAStablePrefix_AndCountsTowardTheLocalGuard()
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var clientOptions = OpenAiCompatChatClientFactory.BuildClientOptions("http://localhost:1234");
        clientOptions.Transport = new HttpClientPipelineTransport(http);
        using var sdk = new OpenAIClient(new ApiKeyCredential("audit"), clientOptions).GetChatClient("model").AsIChatClient();
        using var local = new LocalReasoningChatClient(sdk);
        using var guard = new ContextGuardChatClient(local, ChatTurnLimits.Default, preservePrefix: true);
        var thinking = "first\n\"thought\" " + new string('x', 8_000);
        List<ChatMessage> history =
        [
            new(ChatRole.User, "First question"),
            new(ChatRole.Assistant, [new TextReasoningContent(thinking[..10]), new TextReasoningContent(thinking[10..]), new TextContent("Answer"), new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["key"] = "value" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]),
            new(ChatRole.User, "Next question"),
        ];
        await foreach (var _ in guard.GetStreamingResponseAsync(history)) { }
        var first = handler.Bodies[0].GetProperty("messages");
        var assistant = first[1];
        Assert.False(assistant.TryGetProperty("reasoning_content", out _));
        Assert.Equal($"<think>{thinking}</think>Answer", assistant.GetProperty("content").GetString());
        Assert.Equal("call-1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.InRange(guard.Turn.PeakTokens, thinking.Length / 4, thinking.Length / 4 + 100);
        history.Add(new(ChatRole.Assistant, [new TextReasoningContent("more thinking"), new TextContent("More") ]));
        history.Add(new(ChatRole.User, "Continue"));
        await guard.GetResponseAsync(history);
        var second = handler.Bodies[1].GetProperty("messages");
        for (var i = 0; i < first.GetArrayLength(); i++) Assert.Equal(first[i].GetRawText(), second[i].GetRawText());
        Assert.Null(history[1].RawRepresentation);
        using var small = new ContextGuardChatClient(local, ChatTurnLimits.Default with { ContextWindowTokens = 1_024, ReserveTokens = 128 }, preservePrefix: true);
        await small.GetResponseAsync(history);
        Assert.True(small.Turn.ContextFull);
        using var cloud = new ContextGuardChatClient(sdk, ChatTurnLimits.Default);
        await cloud.GetResponseAsync(history);
        Assert.InRange(cloud.Turn.PeakTokens, 0, 100);
        Assert.False(handler.Bodies[^1].GetProperty("messages")[1].TryGetProperty("reasoning_content", out _));
        Assert.DoesNotContain(handler.Bodies[0].EnumerateObject(), field => field.Name is "temperature" or "top_p" or "top_k" or "min_p" or "presence_penalty");
        using var countOnly = new ContextGuardChatClient(local, ChatTurnLimits.Default, preservePrefix: true);
        await countOnly.GetResponseAsync([new(ChatRole.Assistant, [new TextReasoningContent("ab"), new TextReasoningContent("cd")])]);
        Assert.Equal("<think>abcd</think>".Length / 4, countOnly.Turn.PeakTokens);
        countOnly.BeginTurn();
        await countOnly.GetResponseAsync([new(ChatRole.Assistant, [new TextReasoningContent(""), new TextContent("Plain")])]);
        Assert.Equal("Plain", handler.Bodies[^1].GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("Plain".Length / 4, countOnly.Turn.PeakTokens);
        using var factoryClient = new OpenAiCompatChatClientFactory().Create(new Cockpit.Core.Profiles.LmStudioConfig("http://localhost:1234", "model"));
        Assert.IsType<LocalReasoningChatClient>(factoryClient);
    }

    internal sealed class CapturingHandler : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(body);
            var streaming = body.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            var json = streaming
                ? "data: {\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Done\"},\"finish_reason\":\"stop\"}]}";
            var content = new StreamContent(new ResponseStream(Encoding.UTF8.GetBytes(json)));
            content.Headers.ContentType = new(streaming ? "text/event-stream" : "application/json");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class ResponseStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
