namespace Cockpit.Core.Profiles;

/// <summary>
/// A distinct identity a session runs under: which provider it talks to and the settings that
/// provider needs. Lets the cockpit run several independent identities side by side without mixing
/// their state — two Claude logins (personal + work), a local Ollama model, a plugin-backed provider.
/// <see cref="ProviderConfig"/> selects the provider; <see langword="null"/> means the Claude CLI,
/// which uses the <see cref="ConfigDir"/>/<see cref="ExecutablePath"/> fields below instead.
/// </summary>
/// <param name="Label">Display name shown in the profile picker and on a session panel.</param>
/// <param name="ConfigDir">
/// Claude-CLI profiles only: the directory used as <c>CLAUDE_CONFIG_DIR</c> for a session spawned
/// under this profile, holding its <c>.credentials.json</c> and <c>.claude.json</c>. Empty for a
/// profile that runs under any other provider.
/// </param>
/// <param name="ExecutablePath">
/// Executable to spawn. <see langword="null"/> means "resolve the bundled/default executable
/// at spawn time" (see <see cref="IClaudeExecutableLocator"/>).
/// </param>
/// <param name="Purpose">Short free-text description of what this profile is for.</param>
/// <param name="Defaults">
/// Start defaults (mode/model/effort) the New-session dialog pre-selects for this profile.
/// <see langword="null"/> falls back to the app defaults.
/// </param>
/// <param name="ProviderConfig">
/// The provider this profile runs under (#26). <see langword="null"/> means the Claude-CLI provider,
/// which uses the <see cref="ConfigDir"/>/<see cref="ExecutablePath"/> fields above; an
/// <see cref="OllamaConfig"/>/<see cref="LmStudioConfig"/> selects a local HTTP provider instead.
/// Fixed at creation — a different provider means a new profile.
/// </param>
public sealed record SessionProfile(
    string Label,
    string ConfigDir,
    string? ExecutablePath = null,
    string? Purpose = null,
    ProfileDefaults? Defaults = null,
    ProviderConfig? ProviderConfig = null,
    DelegationPolicy? Delegation = null)
{
    /// <summary>What this profile allows when another session delegates work to it (#67); no policy means it is not a target.</summary>
    public DelegationPolicy DelegationPolicy => Delegation ?? DelegationPolicy.None;

    /// <summary>Which backend drives this profile — <see cref="SessionProvider.ClaudeCli"/> when no <see cref="ProviderConfig"/> is set.</summary>
    public SessionProvider Provider => ProviderConfig?.Provider ?? SessionProvider.ClaudeCli;
}
