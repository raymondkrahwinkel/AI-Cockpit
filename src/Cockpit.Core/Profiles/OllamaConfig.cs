using System.Text.Json;

namespace Cockpit.Core.Profiles;

// Connection settings for an Ollama profile. `BaseUrl`: e.g. `http://localhost:11434`. `Model`: id from `/v1/models`.
// `SystemPrompt`: optional base system prompt sent as the first message of every conversation for this profile.
// `TurnLimits`: optional bounds for the chat turn's context guard (AC-1489), opaque here; absent means its defaults.
public sealed record OllamaConfig(string BaseUrl, string Model, string? SystemPrompt = null, JsonElement? TurnLimits = null) : ProviderConfig(SessionProvider.Ollama);
