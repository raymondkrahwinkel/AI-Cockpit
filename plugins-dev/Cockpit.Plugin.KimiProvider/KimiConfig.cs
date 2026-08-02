using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `KimiProviderConfigView`/`KimiAcpSessionDriverFactory` via the opaque
// `ConfigJson` the host round-trips (AC-268). Mirrors the shape of
// `Cockpit.Plugin.CliAgentProvider.CliAgentConfig`.
//
// `Command`: Path to the `kimi` executable, or a bare name resolved against PATH — see `KimiExecutableLocator`.
// `WorkingDirectory`: Fallback working directory for the spawned process when a session does not supply its own (mirrors `CliAgentConfig.WorkingDirectory`); falls back further to the cockpit's own directory when also empty.
// `DefaultModel`: Optional model id to prefer once a session exists; `null` lets `kimi acp`'s own `configOptions` snapshot decide (the model id's exact form is not hardcoded here — see the design doc's open point on this).
// `AuthEnvVar`: Name of the environment variable the API key is set under for this spawn (never passed as an argument — visible in the process list otherwise). `null`/empty when relying on `kimi acp --login`'s own cached auth instead.
// `ApiKey`: The secret itself — never logged/serialized in the clear, see `ToString`.
internal sealed record KimiConfig(
    string Command = "kimi",
    string WorkingDirectory = "",
    string? DefaultModel = null,
    string? AuthEnvVar = "KIMI_API_KEY",
    string? ApiKey = null)
{
    // Case-insensitive property matching on deserialize — a hand-edited `cockpit.json` should not fail
    // to parse over a casing mismatch, same rationale as `CliAgentConfig.JsonOptions`.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // The environment overlay for the spawned `kimi acp` process. The API key is set as an env-var
    // (never a CLI argument, which would be visible in the process list) only when both an
    // `AuthEnvVar` and an `ApiKey` are present.
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
    // in the clear — anywhere this config lands in a log line or exception message (mirrors `CliAgentConfig.ToString()`).
    public override string ToString() =>
        $"{nameof(KimiConfig)} {{ Command = {Command}, WorkingDirectory = {WorkingDirectory}, DefaultModel = {DefaultModel}, " +
        $"AuthEnvVar = {AuthEnvVar}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")} }}";
}
