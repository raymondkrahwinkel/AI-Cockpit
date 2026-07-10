namespace Cockpit.Core.Profiles;

/// <summary>Connection settings for an LM Studio profile: its OpenAI-compatible server, the model, and an optional API key (only needed behind a key-protected proxy).</summary>
/// <param name="BaseUrl">Server base URL, e.g. <c>http://localhost:1234</c>.</param>
/// <param name="Model">Model id as reported by <c>/v1/models</c>.</param>
/// <param name="ApiKey">Bearer key when the server is key-protected; <see langword="null"/> for the usual local setup.</param>
public sealed record LmStudioConfig(string BaseUrl, string Model, string? ApiKey = null) : ProviderConfig(SessionProvider.LmStudio);
