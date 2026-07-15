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
    /// Claude-only plumbing that stays host-side after Fase 4 (the config directory the read-aloud/status transcript
    /// tailers locate the JSONL under, the login check) asks for this rather than reading fields off the profile.
    /// A profile is a Claude one whether it still carries a legacy <see cref="ClaudeConfig"/> or the bundled Claude
    /// provider plugin's config; the latter is reconstructed from that plugin's opaque config here so those host-side
    /// consumers keep working after a profile is migrated to the plugin (they saw <see langword="null"/> otherwise).
    /// </summary>
    public ClaudeConfig? Claude => ProviderConfig switch
    {
        ClaudeConfig claude => claude,
        PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId } plugin => ClaudePluginProfile.ReadClaudeConfig(plugin.ConfigJson),
        _ => null,
    };
}
