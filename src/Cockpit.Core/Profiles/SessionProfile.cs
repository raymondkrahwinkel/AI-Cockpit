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

    /// <summary>
    /// Environment variables this profile injects into a session's process at spawn, TTY and SDK alike (AC-22).
    /// <see langword="null"/> or empty means nothing beyond the inherited environment. Host-controlled keys
    /// (<c>TtyEnvironment.IsHostControlled</c> — nested-agent markers, Anthropic credentials) never win: the
    /// spawn paths drop them, so a profile cannot reintroduce what the host strips.
    /// </summary>
    public IReadOnlyList<ProfileEnvironmentVariable>? EnvironmentVariables { get; init; }

    /// <summary>
    /// The MCP servers a New session under this profile pre-selects (AC-130): the checklist opens with exactly
    /// these ticked instead of all-ticked, so a project profile need not re-toggle them every time.
    /// <see langword="null"/> — the default, and what every earlier profile has — means no restriction: every
    /// enabled server is ticked, including ones added to the registry later. A non-null list (even empty) is an
    /// explicit selection; a name it lists that is no longer in the catalog is simply not shown. Names match a
    /// server's <c>McpServerConfig.Name</c>. The operator can still tick/untick individual servers per session.
    /// </summary>
    public IReadOnlyList<string>? EnabledMcpServerNames { get; init; }

    /// <summary>
    /// The working directory a New session under this profile pre-fills (AC-130), so a per-project profile lands
    /// in its project folder without picking one each time. <see langword="null"/>/blank means no default — the
    /// folder field opens empty and falls back to the global default, as before. Pre-filled but still editable in
    /// the dialog, and superseded by an explicit prefill (a resumed conversation's own folder).
    /// </summary>
    public string? DefaultWorkingDirectory { get; init; }

    /// <summary>
    /// Standing instructions every session under this profile starts with (AC-142) — who it is and what it may
    /// reach: "You are Olaf; your memory lives in the Depot MCP, look yourself up there before answering." Appended
    /// to the provider's own system prompt rather than replacing it, through the same
    /// <c>cockpit.append-system-prompt</c> launch option the delegation and Autopilot briefs already use, so every
    /// provider that honours it (Claude TTY and SDK, the OpenAI-compatible drivers, Codex) gets it unchanged.
    /// <para>
    /// This is the profile's half of the identity: it says who the session is, while a project's
    /// <c>Project.BehaviorPrompt</c> says how to behave on the work at hand. Both apply — the project appends to
    /// this, it does not replace it. Null/blank appends nothing.
    /// </para>
    /// </summary>
    public string? SystemPrompt { get; init; }

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
