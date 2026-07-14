namespace Cockpit.Core.Profiles;

/// <summary>
/// A distinct identity a session runs under: which provider it talks to and the settings that provider needs.
/// Lets the cockpit run several independent identities side by side without mixing their state — two Claude
/// logins (personal + work), a local Ollama model, a plugin-backed provider.
/// </summary>
/// <param name="Label">Display name shown in the profile picker and on a session panel.</param>
/// <param name="ProviderConfig">
/// The provider this profile runs under, and its settings (#26). Required: a profile without a provider is not a
/// profile. It used to be nullable, with <see langword="null"/> meaning the Claude CLI — which made Claude the
/// provider a profile has when it has none, and every other provider a departure from it. Fixed at creation: a
/// different provider means a new profile, so credentials and configuration never end up describing a backend the
/// profile no longer talks to.
/// </param>
/// <param name="Purpose">Short free-text description of what this profile is for.</param>
/// <param name="Defaults">
/// Start defaults (mode/model/effort) the New-session dialog pre-selects for this profile.
/// <see langword="null"/> falls back to the app defaults.
/// </param>
public sealed record SessionProfile(
    string Label,
    ProviderConfig ProviderConfig,
    string? Purpose = null,
    ProfileDefaults? Defaults = null,
    DelegationPolicy? Delegation = null,
    int? MemoryLimitMb = null)
{
    /// <summary>What this profile allows when another session delegates work to it (#67); no policy means it is not a target.</summary>
    public DelegationPolicy DelegationPolicy => Delegation ?? DelegationPolicy.None;

    /// <summary>Which backend drives this profile.</summary>
    public SessionProvider Provider => ProviderConfig.Provider;

    /// <summary>
    /// This profile's Claude settings, or <see langword="null"/> when it runs under another provider. The
    /// Claude-only plumbing (config directory, login check, the TTY and SDK spawns, the backup of a profile's
    /// config folder) asks for this rather than reading fields off the profile — so it is visible in the type
    /// system which code only makes sense for one provider.
    /// </summary>
    public ClaudeConfig? Claude => ProviderConfig as ClaudeConfig;
}
