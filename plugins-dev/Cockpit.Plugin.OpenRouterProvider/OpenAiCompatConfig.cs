using System.Text.Json;

namespace Cockpit.Plugin.OpenRouterProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `OpenAiCompatProviderConfigView`/`OpenAiCompatPluginSessionDriverFactory` via the
// opaque `ConfigJson` the host round-trips (#45/AC-806). `Model` carries OpenRouter's vendor/model
// notation (e.g. `anthropic/claude-sonnet-4.5`) verbatim — it is just the `ModelId` this plugin's driver
// hands the OpenAI SDK, so no parsing of the vendor prefix happens on this side.
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl)
{
    // Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    // driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    // `cockpit.json` should not fail to parse over a casing mismatch.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // (the OpenRouter API key) in the clear — anywhere this config lands in a log line or exception message.
    public override string ToString() =>
        $"{nameof(OpenAiCompatConfig)} {{ ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, Model = {Model}, BaseUrl = {BaseUrl} }}";
}
