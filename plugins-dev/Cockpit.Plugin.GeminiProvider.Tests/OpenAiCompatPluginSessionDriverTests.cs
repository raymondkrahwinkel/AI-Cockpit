using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.OpenAiCompat;
using NSubstitute;
using OpenAI;

namespace Cockpit.Plugin.GeminiProvider.Tests;

// `OpenAiCompatPluginSessionDriver` against a fake `IChatClient` (#45) — mirrors
// the host's own `OpenAiCompatSessionDriverTests` for the same history/streaming/error-handling shape,
// A session that gets no host toolset stays chat-only; the tool loop it runs when it does get one is covered
// by OpenAiCompatPluginSessionDriverToolLoopTests in the Gemini provider's tests (AC-964, one shared driver).
public class OpenAiCompatPluginSessionDriverTests
{
    [Fact]
    public async Task SendUserMessage_StreamsAssistantDeltas_ThenCompletesTheTurn()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(_NoiseStream(), _Stream("", "\n\n", "  Hello", " ", "world.", "\n    code", "\n"));
        var driver = new OpenAiCompatPluginSessionDriver(chatClient, "gemini-2.5-flash");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("noise only");
        var noise = await _CollectUntilTurnCompletedAsync(driver);
        Assert.Empty(noise.OfType<PluginAssistantTextDelta>());
        Assert.False(Assert.Single(noise.OfType<PluginTurnCompleted>()).IsError);
        Assert.Single(noise, evt => evt is PluginSessionInitialized);

        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Equal("  Hello world.\n    code\n", string.Concat(events.OfType<PluginAssistantTextDelta>().Select(delta => delta.Text)));
        Assert.False(Assert.Single(events.OfType<PluginTurnCompleted>()).IsError);
    }

    // AC-2406, category D: the usage chunk is a third party's wire format, read through the real SDK client.
    [Theory]
    // OpenAI's documented shape: prompt_tokens includes what the cache served.
    [InlineData("""{"prompt_tokens":2006,"completion_tokens":300,"total_tokens":2306,"prompt_tokens_details":{"cached_tokens":1920}}""", 86, 300, 1920, true)]
    // LM Studio's answer as measured for AC-2406: no cache field, so the cache is unknown.
    [InlineData("""{"prompt_tokens":2023,"completion_tokens":60,"total_tokens":2083,"completion_tokens_details":{"reasoning_tokens":57}}""", 2023, 60, 0, false)]
    public async Task SendUserMessage_ReportsTheUsageOfTheStreamsUsageChunk(string usage, int input, int output, int cacheRead, bool cacheReported)
    {
        var sse = $$"""
            data: {"id":"c","object":"chat.completion.chunk","created":1,"model":"m","choices":[{"index":0,"delta":{"content":"ok"},"finish_reason":"stop"}]}

            data: {"id":"c","object":"chat.completion.chunk","created":1,"model":"m","choices":[],"usage":{{usage}}}

            data: [DONE]


            """;
        using var httpClient = new HttpClient(new SseHandler(sse));
        var options = new OpenAIClientOptions { Endpoint = new Uri("http://localhost/v1"), Transport = new HttpClientPipelineTransport(httpClient) };
        var driver = new OpenAiCompatPluginSessionDriver(new OpenAIClient(new ApiKeyCredential("key"), options).GetChatClient("m").AsIChatClient(), "m");

        await driver.StartAsync();
        await driver.SendUserMessageAsync("hi");
        var events = await _CollectUntilTurnCompletedAsync(driver);

        Assert.Equal(new PluginTokenUsage(input, output, cacheRead, 0) { CacheReported = cacheReported }, Assert.Single(events.OfType<PluginTurnCompleted>()).Usage);
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _NoiseStream()
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, "");
        yield return new ChatResponseUpdate(ChatRole.Assistant, " \t\n");
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextReasoningContent("reasoning without content")],
        };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> _Stream(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        await Task.CompletedTask;
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
