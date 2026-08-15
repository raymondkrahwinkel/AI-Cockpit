using System.Text.Json;

namespace Cockpit.Plugin.GrokProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `OpenAiCompatProviderConfigView`/`OpenAiCompatPluginSessionDriverFactory` via the
// opaque `ConfigJson` the host round-trips (#45/AC-724). `Model` carries the xAI model id (e.g.
// `grok-4.6`) verbatim — no default is hardcoded anywhere in this plugin (AC-724 criterion 4): xAI
// deprecated four model names on 2026-05-15 alone, so a baked-in default would go stale the same way.
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl)
{
    // Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    // driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    // `cockpit.json` should not fail to parse over a casing mismatch.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // (the xAI API key) in the clear — anywhere this config lands in a log line or exception message.
    public override string ToString() =>
        $"{nameof(OpenAiCompatConfig)} {{ ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, Model = {Model}, BaseUrl = {BaseUrl} }}";
}
