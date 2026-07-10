namespace Cockpit.Core.Profiles;

/// <summary>Connection settings for an Ollama profile: its OpenAI-compatible server and the model to run.</summary>
/// <param name="BaseUrl">Server base URL, e.g. <c>http://localhost:11434</c>.</param>
/// <param name="Model">Model id as reported by <c>/v1/models</c>.</param>
public sealed record OllamaConfig(string BaseUrl, string Model) : ProviderConfig(SessionProvider.Ollama);
