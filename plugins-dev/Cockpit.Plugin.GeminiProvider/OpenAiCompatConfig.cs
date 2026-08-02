using System.Text.Json;

namespace Cockpit.Plugin.GeminiProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `OpenAiCompatProviderConfigView`/`OpenAiCompatPluginSessionDriverFactory` via the
// opaque `ConfigJson` the host round-trips (#45). One shape covers both providers this plugin
// registers (Gemini and OpenAI) — they differ only in which base URL a profile carries.
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl)
{
    // Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    // driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    // `cockpit.json` should not fail to parse over a casing mismatch.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message.
    public override string ToString() =>
        $"{nameof(OpenAiCompatConfig)} {{ ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, Model = {Model}, BaseUrl = {BaseUrl} }}";
}
