namespace Cockpit.Core.Profiles;

// Connection settings for an Ollama profile: its OpenAI-compatible server and the model to run.
// `BaseUrl`: server base URL, e.g. `http://localhost:11434`. `Model`: id as reported by `/v1/models`.
// `SystemPrompt`: optional base system prompt sent as the first message of every conversation for this profile.
public sealed record OllamaConfig(string BaseUrl, string Model, string? SystemPrompt = null) : ProviderConfig(SessionProvider.Ollama);
