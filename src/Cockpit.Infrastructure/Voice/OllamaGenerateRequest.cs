using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Request body for Ollama's <c>POST /api/generate</c>, shaped 1:1 to WisperFlow's <c>cleanup.py</c> call.</summary>
internal sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("system")]
    public required string System { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("options")]
    public OllamaGenerateOptions? Options { get; init; }
}

internal sealed class OllamaGenerateOptions
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; init; }

    [JsonPropertyName("seed")]
    public int Seed { get; init; }
}
