namespace Cockpit.Core.Profiles;

// Connection settings for an Ollama profile: its OpenAI-compatible server and the model to run.
//
// `BaseUrl`: Server base URL, e.g. `http://localhost:11434`.
// `Model`: Model id as reported by `/v1/models`.
// `SystemPrompt`: Optional base system prompt sent as the first (system) message of every conversation for this profile.
public sealed record OllamaConfig(string BaseUrl, string Model, string? SystemPrompt = null) : ProviderConfig(SessionProvider.Ollama);
