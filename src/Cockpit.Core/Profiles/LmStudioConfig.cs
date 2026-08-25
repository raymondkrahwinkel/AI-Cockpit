namespace Cockpit.Core.Profiles;

// Connection settings for an LM Studio profile: its OpenAI-compatible server, the model, and an optional API key
// (only needed behind a key-protected proxy). `BaseUrl`: e.g. `http://localhost:1234`. `Model`: id from `/v1/models`.
// `ApiKey`: bearer key when protected, `null` otherwise. `SystemPrompt`: optional base system prompt for every conversation.
public sealed record LmStudioConfig(string BaseUrl, string Model, string? ApiKey = null, string? SystemPrompt = null) : ProviderConfig(SessionProvider.LmStudio)
{
    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message (a leak surface, not just
    // a display concern; DPAPI-at-rest for the stored value is a separate, later decision).
    public override string ToString() =>
        $"{nameof(LmStudioConfig)} {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, SystemPrompt = {SystemPrompt} }}";
}
