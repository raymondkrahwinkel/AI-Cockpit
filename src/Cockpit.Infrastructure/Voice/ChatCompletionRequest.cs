using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Request body for an OpenAI-compatible <c>POST /v1/chat/completions</c> — the shape Ollama and LM Studio both accept.</summary>
internal sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatCompletionMessage> Messages { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("seed")]
    public int Seed { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }
}

/// <summary>One chat message — used both to build the request (system/user) and to read the reply (assistant).</summary>
internal sealed class ChatCompletionMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}
