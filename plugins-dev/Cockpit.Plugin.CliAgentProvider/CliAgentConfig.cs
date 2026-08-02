using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
// `CliAgentProviderConfigView`/`CliSubprocessPluginSessionDriverFactory` via the
// opaque `ConfigJson` the host round-trips (#45 fase B1). Mirrors the shape from the design doc §2.5.
//
// `Command`: Path to the CLI executable, or a bare name (e.g. `"codex"`) resolved against PATH — see `CliExecutableLocator`. Cross-platform npm-shim discovery is a B2 refinement.
// `SubCommand`: The CLI subcommand that enters headless mode, e.g. `"exec"` for Codex.
// `PromptMode`: `"arg"` (prompt passed as a CLI argument) or `"stdin"` (prompt piped to stdin after spawn).
// `OutputFormatArgs`: Flags that switch the CLI to JSONL output, e.g. `["--json"]` for Codex. `null` falls back to `EffectiveOutputFormatArgs`'s Codex default.
// `Model`: Optional model id passed as `-m &lt;model&gt;`; `null`/empty lets the CLI use its own default.
// `WorkingDirectory`: The child process's working directory — also its sandbox root.
// `SandboxMode`: Passed as `--sandbox &lt;value&gt;`; Codex's own default is `"read-only"` (safe) — `"workspace-write"`/`"danger-full-access"` only on explicit operator choice.
// `ExtraArgs`: Any additional CLI flags appended verbatim, e.g. `["--skip-git-repo-check"]`.
// `AuthEnvVar`: Name of the environment variable the API key is set under for this spawn (never passed as an argument — visible in the process list otherwise). `null`/empty when relying on `codex login`'s own cached auth instead.
// `ApiKey`: The secret itself — never logged/serialized in the clear, see `ToString`.
// `ConfigDir`: Optional CLI config/home directory override (Codex: `CODEX_HOME`); empty uses the CLI's own default.
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

    // The environment overlay for a spawned CLI process — shared by both the exec and app-server drivers so
    // the auth/config-dir handling lives in one place. The API key is set as an env-var (never a CLI argument,
    // which would be visible in the process list) only when both an `AuthEnvVar` and an
    // `ApiKey` are present; `ConfigDir` maps to Codex's `CODEX_HOME`.
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
