using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: this plugin's own provider config, never seen by the host — only (de)serialized here via the
// opaque `ConfigJson` the host round-trips. Mirrors the shape of `Cockpit.Plugin.KimiProvider.KimiConfig`.
// `AuthEnvVar` defaults to `OPENCODE_API_KEY`, the env var opencode's own ACP integration docs use.
internal sealed record OpencodeConfig(
    string Command = "opencode",
    string WorkingDirectory = "",
    string? DefaultModel = null,
    string? AuthEnvVar = "OPENCODE_API_KEY",
    string? ApiKey = null)
{
    // Case-insensitive property matching on deserialize — a hand-edited `cockpit.json` should not fail
    // to parse over a casing mismatch, same rationale as `KimiConfig.JsonOptions`.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // The environment overlay for the spawned `opencode acp` process. The API key is set as an env-var
    // (never a CLI argument, which would be visible in the process list) only when both an `AuthEnvVar` and
    // an `ApiKey` are present.
    public Dictionary<string, string?> BuildEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(AuthEnvVar) && !string.IsNullOrEmpty(ApiKey))
        {
            environmentVariables[AuthEnvVar] = ApiKey;
        }

        return environmentVariables;
    }

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message (mirrors `KimiConfig.ToString()`).
    public override string ToString() =>
        $"{nameof(OpencodeConfig)} {{ Command = {Command}, WorkingDirectory = {WorkingDirectory}, DefaultModel = {DefaultModel}, " +
        $"AuthEnvVar = {AuthEnvVar}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")} }}";
}
