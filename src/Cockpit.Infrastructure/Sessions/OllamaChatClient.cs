using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.OpenAiCompat;
using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class OllamaChatClient(OllamaConfig config) : IChatClient
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Uri _endpoint = new($"{config.BaseUrl.TrimEnd('/')}/api/chat");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(messages, options, false, cancellationToken).ToChatResponseAsync(cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _ReadAsync(messages, options, true, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> _ReadAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = _Request(messages, options, streaming);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var errorReader = new StreamReader(stream);
            var error = new char[2000];
            var length = await errorReader.ReadBlockAsync(error.AsMemory(), cancellationToken).ConfigureAwait(false);
            var retry = response.Headers.RetryAfter;
            throw new OllamaChatException(response.StatusCode, new string(error, 0, length),
                retry?.Date ?? (retry?.Delta is { } delta ? DateTimeOffset.UtcNow.Add(delta) : null));
        }

        var responseId = Guid.NewGuid().ToString("N");
        var callIndex = 0;
        var hasToolCalls = false;
        if (!streaming)
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            yield return _Update(document.RootElement, responseId, ref callIndex, ref hasToolCalls);
            if (!document.RootElement.TryGetProperty("done", out var done) || !done.GetBoolean())
                throw new IOException("Ollama chat response ended before its done packet.");
            yield break;
        }

        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var packet = document.RootElement;
            yield return _Update(packet, responseId, ref callIndex, ref hasToolCalls);
            if (packet.TryGetProperty("done", out var done) && done.GetBoolean()) yield break;
        }
        throw new IOException("Ollama chat stream ended before its done packet.");
    }

    private JsonObject _Request(IEnumerable<ChatMessage> messages, ChatOptions? options, bool streaming)
    {
        var nativeOptions = new JsonObject { ["num_ctx"] = LocalModelContext.Normalize(config.ContextTokens) };
        if (options?.Temperature is { } temperature) nativeOptions["temperature"] = temperature;
        if (options?.TopP is { } topP) nativeOptions["top_p"] = topP;
        if (options?.TopK is { } topK) nativeOptions["top_k"] = topK;
        if (options?.MaxOutputTokens is { } maxTokens) nativeOptions["num_predict"] = maxTokens;
        if (options?.Seed is { } seed) nativeOptions["seed"] = seed;
        if (options?.StopSequences is { Count: > 0 } stops) nativeOptions["stop"] = JsonSerializer.SerializeToNode(stops);
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? config.Model,
            ["messages"] = _Messages(messages),
            ["stream"] = streaming,
            ["options"] = nativeOptions,
        };
        if (!string.IsNullOrWhiteSpace(config.ReasoningEffort)) body["think"] = config.ReasoningEffort.Trim();
        if (options?.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.OfType<AIFunction>().Select(function => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText()),
                },
            }).ToArray());
        }
        return body;
    }

    private static JsonArray _Messages(IEnumerable<ChatMessage> messages)
    {
        var result = new JsonArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
            foreach (var call in calls) names[call.CallId] = call.Name;
            if (message.Role == ChatRole.Tool)
            {
                foreach (var toolResult in message.Contents.OfType<FunctionResultContent>())
                {
                    result.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_name"] = names.TryGetValue(toolResult.CallId, out var name) ? name : throw new InvalidOperationException("Ollama tool result has no matching call."),
                        ["content"] = ChatTurnLoop.ResultText(toolResult.Result) ?? string.Empty,
                    });
                }
                continue;
            }
            var native = new JsonObject { ["role"] = message.Role.Value, ["content"] = message.Text };
            var thinking = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
            if (thinking.Length > 0) native["thinking"] = thinking;
            var images = message.Contents.OfType<DataContent>().Where(content => content.HasTopLevelMediaType("image")).ToArray();
            if (images.Length > 0) native["images"] = new JsonArray(images.Select(content => (JsonNode?)JsonValue.Create(Convert.ToBase64String(content.Data.Span))).ToArray());
            if (calls.Length > 0)
            {
                native["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = JsonSerializer.SerializeToNode(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
                    },
                }).ToArray());
            }
            result.Add(native);
        }
        return result;
    }

    private static ChatResponseUpdate _Update(JsonElement packet, string responseId, ref int callIndex, ref bool hasToolCalls)
    {
        if (packet.TryGetProperty("error", out var error))
        {
            var text = error.GetString() ?? "Unknown stream error";
            throw new InvalidOperationException($"Ollama: {text[..Math.Min(text.Length, 2000)]}");
        }
        var update = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            ResponseId = responseId,
            MessageId = responseId,
            ModelId = packet.TryGetProperty("model", out var model) ? model.GetString() : null,
        };
        if (packet.TryGetProperty("message", out var message))
        {
            if (message.TryGetProperty("thinking", out var thinking) && thinking.GetString() is { Length: > 0 } thought)
                update.Contents.Add(new TextReasoningContent(thought));
            if (message.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text)
                update.Contents.Add(new TextContent(text));
            if (message.TryGetProperty("tool_calls", out var calls))
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    var arguments = function.GetProperty("arguments").Deserialize<Dictionary<string, object?>>();
                    update.Contents.Add(new FunctionCallContent($"call_{responseId}_{callIndex++}", function.GetProperty("name").GetString()!, arguments));
                    hasToolCalls = true;
                }
            }
        }
        if (packet.TryGetProperty("done", out var done) && done.GetBoolean())
        {
            var reason = packet.TryGetProperty("done_reason", out var finish) ? finish.GetString() : "stop";
            update.FinishReason = hasToolCalls ? ChatFinishReason.ToolCalls : reason == "length" ? ChatFinishReason.Length : ChatFinishReason.Stop;
            update.Contents.Add(new UsageContent(new UsageDetails
            {
                InputTokenCount = packet.TryGetProperty("prompt_eval_count", out var input) ? input.GetInt64() : null,
                OutputTokenCount = packet.TryGetProperty("eval_count", out var output) ? output.GetInt64() : null,
            }));
        }
        return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is not null ? null
        : serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("ollama", _endpoint, config.Model)
        : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();
}
