using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `OpencodeProviderConfigView`/`OpencodeAcpSessionDriverFactory` via the opaque `ConfigJson` the host
// round-trips (AC-783). Mirrors the shape of `Cockpit.Plugin.KimiProvider.KimiConfig`.
//
// `Command`: Path to the `opencode` executable, or a bare name resolved against PATH — see `OpencodeExecutableLocator`.
// `WorkingDirectory`: Fallback working directory for the spawned process when a session does not supply its own; falls back further to the cockpit's own directory when also empty.
// `DefaultModel`: Optional model id to prefer once a session exists, in opencode's `provider/model` notation (e.g. `anthropic/claude-sonnet-4-5`); `null` lets opencode's own `configOptions` snapshot decide.
// `AuthEnvVar`: Name of the environment variable the API key is set under for this spawn (never passed as an argument — visible in the process list otherwise). Defaults to `OPENCODE_API_KEY` — documented on opencode.ai's ACP integration page as the variable ACP clients pass through (e.g. the Avante.nvim example: `env = { OPENCODE_API_KEY = ... }`). `null`/empty when relying on `opencode auth login`'s own cached auth, or a model that needs no key at all (opencode ships free models under `opencode/*` that this session's own testing used without any auth).
// `ApiKey`: The secret itself — never logged/serialized in the clear, see `ToString`.
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
