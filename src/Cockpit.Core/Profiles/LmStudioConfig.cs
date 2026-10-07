using System.Text.Json;

namespace Cockpit.Core.Profiles;

// Connection settings for an LM Studio profile. `BaseUrl`: e.g. `http://localhost:1234`. `Model`: id from `/v1/models`.
// `ApiKey`: bearer key behind a key-protected proxy, `null` otherwise. `SystemPrompt`: optional base system prompt.
// `TurnLimits`: optional bounds for the chat turn's context guard (AC-1489), opaque here; absent means its defaults.
public sealed record LmStudioConfig(string BaseUrl, string Model, string? ApiKey = null, string? SystemPrompt = null, JsonElement? TurnLimits = null) : ProviderConfig(SessionProvider.LmStudio)
{
    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message (a leak surface, not just
    // a display concern; DPAPI-at-rest for the stored value is a separate, later decision).
    public override string ToString() =>
        $"{nameof(LmStudioConfig)} {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, SystemPrompt = {SystemPrompt} }}";
}
