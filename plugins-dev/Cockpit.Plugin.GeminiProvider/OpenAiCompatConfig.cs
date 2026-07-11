using System.Text.Json;

namespace Cockpit.Plugin.GeminiProvider;

/// <summary>
/// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
/// <see cref="OpenAiCompatProviderConfigView"/>/<see cref="OpenAiCompatPluginSessionDriverFactory"/> via the
/// opaque <c>ConfigJson</c> the host round-trips (#45). One shape covers both providers this plugin
/// registers (Gemini and OpenAI) — they differ only in which base URL a profile carries.
/// </summary>
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl)
{
    /// <summary>
    /// Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    /// driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    /// <c>cockpit.json</c> should not fail to parse over a casing mismatch.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
