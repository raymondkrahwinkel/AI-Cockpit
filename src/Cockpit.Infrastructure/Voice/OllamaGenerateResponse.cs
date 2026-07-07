using System.Text.Json.Serialization;

namespace Cockpit.Infrastructure.Voice;

/// <summary>Response body from Ollama's <c>POST /api/generate</c> (non-streaming) — only the field the cleanup step needs.</summary>
internal sealed class OllamaGenerateResponse
{
    [JsonPropertyName("response")]
    public string? Response { get; init; }
}
