using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Response body for an OpenAI-compatible <c>POST /v1/chat/completions</c> — only the first choice's message is read.</summary>
internal sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public IReadOnlyList<ChatCompletionChoice>? Choices { get; init; }
}

internal sealed class ChatCompletionChoice
{
    [JsonPropertyName("message")]
    public ChatCompletionMessage? Message { get; init; }
}
