using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

// AC-724: this plugin's own provider config, never seen by the host — only (de)serialized here via the opaque
// `ConfigJson` the host round-trips. No default model is hardcoded: xAI deprecates model names fast. `TurnLimits`
// (AC-1489) bounds the driver's turns; opaque here, so the config view carries it through a save untouched.
internal sealed record OpenAiCompatConfig(string ApiKey, string Model, string BaseUrl, JsonElement? TurnLimits = null)
{
    // Case-insensitive property matching on deserialize — the two call sites (this plugin's own view and
    // driver factory) always agree on casing already, but a config JSON that ends up hand-edited in
    // `cockpit.json` should not fail to parse over a casing mismatch.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // AC-1484: the variable a profile without a key of its own falls back to, the container secret in session.env.
    public const string ApiKeyEnvVar = "XAI_API_KEY";

    // The profile's own key wins; an empty one falls back to the environment, but only for the provider's own host.
    public static string? ResolveApiKey(OpenAiCompatConfig config, string? envVar, string defaultBaseUrl) =>
        !string.IsNullOrWhiteSpace(config.ApiKey) ? config.ApiKey : _FromEnvironment(envVar, config.BaseUrl, defaultBaseUrl);

    // Says where the key comes from without reading its value into anything: the profile, the environment, or nowhere.
    public static PluginCredentialKind CredentialKindOf(string configJson, string? envVar, string defaultBaseUrl)
    {
        OpenAiCompatConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, JsonOptions);
        }
        catch (JsonException)
        {
            return PluginCredentialKind.Unknown;
        }

        if (config is null)
        {
            return PluginCredentialKind.Unknown;
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            return PluginCredentialKind.ApiKey;
        }

        return _FromEnvironment(envVar, config.BaseUrl, defaultBaseUrl) is null ? PluginCredentialKind.Unknown : PluginCredentialKind.ApiKeyFromSecret;
    }

    // The environment key goes only to the provider's own host: a profile whose base URL points elsewhere needs its own key.
    public static bool UsesDefaultHost(string? baseUrl, string defaultBaseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var host)
        && Uri.TryCreate(defaultBaseUrl, UriKind.Absolute, out var own)
        && host.Scheme == own.Scheme
        && string.Equals(host.Host, own.Host, StringComparison.OrdinalIgnoreCase);

    private static string? _FromEnvironment(string? envVar, string? baseUrl, string defaultBaseUrl)
    {
        var value = string.IsNullOrWhiteSpace(envVar) || !UsesDefaultHost(baseUrl, defaultBaseUrl) ? null : Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // (the xAI API key) in the clear — anywhere this config lands in a log line or exception message.
    public override string ToString() =>
        $"{nameof(OpenAiCompatConfig)} {{ ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, Model = {Model}, BaseUrl = {BaseUrl} }}";
}
