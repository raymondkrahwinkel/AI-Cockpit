using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `OpenAiCompatProviderConfigView`/`OpenAiCompatPluginSessionDriverFactory` via the
// opaque `ConfigJson` the host round-trips (#45). One shape covers both providers this plugin
// registers (Gemini and OpenAI) — they differ only in which base URL a profile carries.
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl, int? TimeoutSeconds = null)
{
    // AC-1344: the OpenAI SDK's own NetworkTimeout default (100s) is too short for a reasoning model that
    // "thinks" before its first token on a remote gateway (measured: Hetzner Inference, Qwen3.8-27B, timed
    // out mid-thinking). A profile without this key gets this wider default instead of the SDK's own.
    internal const int DefaultTimeoutSeconds = 600;

    // Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    // driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    // `cockpit.json` should not fail to parse over a casing mismatch.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // AC-1484: the variable a profile without a key of its own falls back to, the container secret in session.env.
    public const string ApiKeyEnvVar = "GEMINI_API_KEY";

    // The profile's own key wins; an empty one falls back to the process environment, or null when there is none.
    public static string? ResolveApiKey(string? configApiKey, string? envVar) =>
        _FromEnvironment(envVar) ?? configApiKey;

    // Says where the key comes from without reading its value into anything: the profile, the environment, or nowhere.
    public static PluginCredentialKind CredentialKindOf(string configJson, string? envVar)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, JsonOptions)?.ApiKey))
            {
                return PluginCredentialKind.ApiKey;
            }
        }
        catch (JsonException)
        {
            return PluginCredentialKind.Unknown;
        }

        return _FromEnvironment(envVar) is null ? PluginCredentialKind.Unknown : PluginCredentialKind.ApiKeyFromSecret;
    }

    private static string? _FromEnvironment(string? envVar)
    {
        var value = string.IsNullOrWhiteSpace(envVar) ? null : Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message.
    public override string ToString() =>
        $"{nameof(OpenAiCompatConfig)} {{ ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, Model = {Model}, BaseUrl = {BaseUrl} }}";
}
