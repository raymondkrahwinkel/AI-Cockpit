using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
/// <see cref="CliAgentProviderConfigView"/>/<see cref="CliSubprocessPluginSessionDriverFactory"/> via the
/// opaque <c>ConfigJson</c> the host round-trips (#45 fase B1). Mirrors the shape from the design doc §2.5.
/// </summary>
/// <param name="Command">Path to the CLI executable, or a bare name (e.g. <c>"codex"</c>) resolved against PATH — see <see cref="CliExecutableLocator"/>. Cross-platform npm-shim discovery is a B2 refinement.</param>
/// <param name="SubCommand">The CLI subcommand that enters headless mode, e.g. <c>"exec"</c> for Codex.</param>
/// <param name="PromptMode"><c>"arg"</c> (prompt passed as a CLI argument) or <c>"stdin"</c> (prompt piped to stdin after spawn).</param>
/// <param name="OutputFormatArgs">Flags that switch the CLI to JSONL output, e.g. <c>["--json"]</c> for Codex. <see langword="null"/> falls back to <see cref="EffectiveOutputFormatArgs"/>'s Codex default.</param>
/// <param name="Model">Optional model id passed as <c>-m &lt;model&gt;</c>; <see langword="null"/>/empty lets the CLI use its own default.</param>
/// <param name="WorkingDirectory">The child process's working directory — also its sandbox root.</param>
/// <param name="SandboxMode">Passed as <c>--sandbox &lt;value&gt;</c>; Codex's own default is <c>"read-only"</c> (safe) — <c>"workspace-write"</c>/<c>"danger-full-access"</c> only on explicit operator choice.</param>
/// <param name="ExtraArgs">Any additional CLI flags appended verbatim, e.g. <c>["--skip-git-repo-check"]</c>.</param>
/// <param name="AuthEnvVar">Name of the environment variable the API key is set under for this spawn (never passed as an argument — visible in the process list otherwise). <see langword="null"/>/empty when relying on <c>codex login</c>'s own cached auth instead.</param>
/// <param name="ApiKey">The secret itself — never logged/serialized in the clear, see <see cref="ToString"/>.</param>
/// <param name="ConfigDir">Optional CLI config/home directory override (Codex: <c>CODEX_HOME</c>); empty uses the CLI's own default.</param>
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
    /// <summary>
    /// Case-insensitive property matching on deserialize — a hand-edited <c>cockpit.json</c> should not fail
    /// to parse over a casing mismatch, same rationale as <c>OpenAiCompatConfig.JsonOptions</c>.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>True when <see cref="PromptMode"/> is <c>"stdin"</c> rather than the <c>"arg"</c> default.</summary>
    public bool IsStdinPromptMode => string.Equals(PromptMode, "stdin", StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="OutputFormatArgs"/>, defaulting to Codex's own <c>--json</c> flag when unset.</summary>
    public IReadOnlyList<string> EffectiveOutputFormatArgs => OutputFormatArgs ?? ["--json"];

    /// <summary><see cref="ExtraArgs"/>, defaulting to an empty list when unset.</summary>
    public IReadOnlyList<string> EffectiveExtraArgs => ExtraArgs ?? [];

    /// <summary>
    /// Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="ApiKey"/>
    /// in the clear — anywhere this config lands in a log line or exception message (mirrors <c>OpenAiCompatConfig.ToString()</c>).
    /// </summary>
    public override string ToString() =>
        $"{nameof(CliAgentConfig)} {{ Command = {Command}, SubCommand = {SubCommand}, PromptMode = {PromptMode}, Model = {Model}, " +
        $"WorkingDirectory = {WorkingDirectory}, SandboxMode = {SandboxMode}, AuthEnvVar = {AuthEnvVar}, " +
        $"ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, ConfigDir = {ConfigDir} }}";
}
