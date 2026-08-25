using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and via
// `ConfigJson` the host round-trips (#45 fase B1). SandboxMode defaults to "read-only"; AuthEnvVar
// keeps the API key out of the process argument list.
internal sealed record CliAgentConfig(
    string Command = "codex",
    string SubCommand = "exec",
    string PromptMode = "arg",
    IReadOnlyList<string>? OutputFormatArgs = null,
    string? Model = null,
    string WorkingDirectory = "",
    string SandboxMode = "read-only",
    IReadOnlyList<string>? ExtraArgs = null,
    string? AuthEnvVar = "CODEX_API_KEY",
    string? ApiKey = null,
    string? ConfigDir = null)
{
    // Case-insensitive property matching on deserialize — a hand-edited `cockpit.json` should not fail
    // to parse over a casing mismatch, same rationale as `OpenAiCompatConfig.JsonOptions`.
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // True when `PromptMode` is `"stdin"` rather than the `"arg"` default.
    public bool IsStdinPromptMode => string.Equals(PromptMode, "stdin", StringComparison.OrdinalIgnoreCase);

    // `OutputFormatArgs`, defaulting to Codex's own `--json` flag when unset.
    public IReadOnlyList<string> EffectiveOutputFormatArgs => OutputFormatArgs ?? ["--json"];

    // `ExtraArgs`, defaulting to an empty list when unset.
    public IReadOnlyList<string> EffectiveExtraArgs => ExtraArgs ?? [];

    // The operator's per-session launch-option choice from the New-session dialog wins; the profile's own
    // configured value is the fallback, and a blank choice counts as absent. Shared by both the TTY provider
    // and the app-server driver so this precedence lives in one place.
    public static string? ResolveOption(IReadOnlyDictionary<string, string>? options, string key, string? fallback) =>
        options is not null && options.TryGetValue(key, out var chosen) && !string.IsNullOrWhiteSpace(chosen) ? chosen : fallback;

    // Shared by both drivers so auth/config-dir handling lives in one place. The API key is set as an
    // env-var — never a CLI argument, which would be visible in the process list — only when both
    // `AuthEnvVar` and `ApiKey` are present; `ConfigDir` maps to Codex's `CODEX_HOME`.
    public Dictionary<string, string?> BuildEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(AuthEnvVar) && !string.IsNullOrEmpty(ApiKey))
        {
            environmentVariables[AuthEnvVar] = ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(ConfigDir))
        {
            environmentVariables["CODEX_HOME"] = ConfigDir;
        }

        return environmentVariables;
    }

    // Overrides the record's auto-generated `ToString()`, which would otherwise print `ApiKey`
    // in the clear — anywhere this config lands in a log line or exception message (mirrors `OpenAiCompatConfig.ToString()`).
    public override string ToString() =>
        $"{nameof(CliAgentConfig)} {{ Command = {Command}, SubCommand = {SubCommand}, PromptMode = {PromptMode}, Model = {Model}, " +
        $"WorkingDirectory = {WorkingDirectory}, SandboxMode = {SandboxMode}, AuthEnvVar = {AuthEnvVar}, " +
        $"ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, ConfigDir = {ConfigDir} }}";
}
