using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// This plugin's own provider config — never seen by the host, only (de)serialized here and inside
/// <see cref="KimiProviderConfigView"/>/<see cref="KimiAcpSessionDriverFactory"/> via the opaque
/// <c>ConfigJson</c> the host round-trips (AC-268). Mirrors the shape of
/// <c>Cockpit.Plugin.CliAgentProvider.CliAgentConfig</c>.
/// </summary>
/// <param name="Command">Path to the <c>kimi</c> executable, or a bare name resolved against PATH — see <see cref="KimiExecutableLocator"/>.</param>
/// <param name="WorkingDirectory">Fallback working directory for the spawned process when a session does not supply its own (mirrors <c>CliAgentConfig.WorkingDirectory</c>); falls back further to the cockpit's own directory when also empty.</param>
/// <param name="DefaultModel">Optional model id to prefer once a session exists; <see langword="null"/> lets <c>kimi acp</c>'s own <c>configOptions</c> snapshot decide (the model id's exact form is not hardcoded here — see the design doc's open point on this).</param>
/// <param name="AuthEnvVar">Name of the environment variable the API key is set under for this spawn (never passed as an argument — visible in the process list otherwise). <see langword="null"/>/empty when relying on <c>kimi acp --login</c>'s own cached auth instead.</param>
/// <param name="ApiKey">The secret itself — never logged/serialized in the clear, see <see cref="ToString"/>.</param>
internal sealed record KimiConfig(
    string Command = "kimi",
    string WorkingDirectory = "",
    string? DefaultModel = null,
    string? AuthEnvVar = "KIMI_API_KEY",
    string? ApiKey = null)
{
    /// <summary>
    /// Case-insensitive property matching on deserialize — a hand-edited <c>cockpit.json</c> should not fail
    /// to parse over a casing mismatch, same rationale as <c>CliAgentConfig.JsonOptions</c>.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The environment overlay for the spawned <c>kimi acp</c> process. The API key is set as an env-var
    /// (never a CLI argument, which would be visible in the process list) only when both an
    /// <see cref="AuthEnvVar"/> and an <see cref="ApiKey"/> are present.
    /// </summary>
    public Dictionary<string, string?> BuildEnvironmentVariables()
    {
        var environmentVariables = new Dictionary<string, string?>();

        if (!string.IsNullOrWhiteSpace(AuthEnvVar) && !string.IsNullOrEmpty(ApiKey))
        {
            environmentVariables[AuthEnvVar] = ApiKey;
        }

        return environmentVariables;
    }

    /// <summary>
    /// Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="ApiKey"/>
    /// in the clear — anywhere this config lands in a log line or exception message (mirrors <c>CliAgentConfig.ToString()</c>).
    /// </summary>
    public override string ToString() =>
        $"{nameof(KimiConfig)} {{ Command = {Command}, WorkingDirectory = {WorkingDirectory}, DefaultModel = {DefaultModel}, " +
        $"AuthEnvVar = {AuthEnvVar}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")} }}";
}
