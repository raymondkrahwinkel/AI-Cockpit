using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Response body for an OpenAI-compatible <c>GET /v1/models</c> — the model list Ollama and LM Studio both expose.</summary>
internal sealed class ModelsResponse
{
    [JsonPropertyName("data")]
    public IReadOnlyList<ModelEntry>? Data { get; init; }
}

internal sealed class ModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
