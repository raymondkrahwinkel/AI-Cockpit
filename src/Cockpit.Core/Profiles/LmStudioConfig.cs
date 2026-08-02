namespace Cockpit.Core.Profiles;

// Connection settings for an LM Studio profile: its OpenAI-compatible server, the model, and an optional API key (only needed behind a key-protected proxy).
//
// `BaseUrl`: Server base URL, e.g. `http://localhost:1234`.
// `Model`: Model id as reported by `/v1/models`.
// `ApiKey`: Bearer key when the server is key-protected; `null` for the usual local setup.
// `SystemPrompt`: Optional base system prompt sent as the first (system) message of every conversation for this profile.
public sealed record LmStudioConfig(string BaseUrl, string Model, string? ApiKey = null, string? SystemPrompt = null) : ProviderConfig(SessionProvider.LmStudio)
{
    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message (a leak surface, not just
    // a display concern; DPAPI-at-rest for the stored value is a separate, later decision).
    public override string ToString() =>
        $"{nameof(LmStudioConfig)} {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, SystemPrompt = {SystemPrompt} }}";
}
