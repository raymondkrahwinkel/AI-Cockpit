namespace Cockpit.Core.Profiles;

// A distinct identity a session runs under: which provider it talks to and the settings that provider needs.
// Lets the cockpit run several independent identities side by side without mixing their state — two Claude
// logins (personal + work), a local Ollama model, a plugin-backed provider.
//
// `Label`: Display name shown in the profile picker and on a session panel.
// `ProviderConfig`:
// The provider this profile runs under, and its settings (#26). Required: a profile without a provider is not a
// profile. It used to be nullable, with `null` meaning the Claude CLI — which made Claude the
// provider a profile has when it has none, and every other provider a departure from it. Fixed at creation: a
// different provider means a new profile, so credentials and configuration never end up describing a backend the
// profile no longer talks to.
// `Purpose`: Short free-text description of what this profile is for.
// `Defaults`:
// Start defaults (mode/model/effort) the New-session dialog pre-selects for this profile.
// `null` falls back to the app defaults.
public sealed record SessionProfile(
    string Label,
    ProviderConfig ProviderConfig,
    string? Purpose = null,
    ProfileDefaults? Defaults = null,
    DelegationPolicy? Delegation = null)
{
    // What this profile allows when another session delegates work to it (#67); no policy means it is not a target.
    public DelegationPolicy DelegationPolicy => Delegation ?? DelegationPolicy.None;

    // Environment variables this profile injects into a session's process at spawn, TTY and SDK alike (AC-22).
    // `null` or empty means nothing beyond the inherited environment. Host-controlled keys
    // (`TtyEnvironment.IsHostControlled` — nested-agent markers, Anthropic credentials) never win: the
    // spawn paths drop them, so a profile cannot reintroduce what the host strips.
    public IReadOnlyList<ProfileEnvironmentVariable>? EnvironmentVariables { get; init; }

    // The MCP servers a New session under this profile pre-selects (AC-130): the checklist opens with exactly
    // these ticked instead of all-ticked, so a project profile need not re-toggle them every time.
    // `null` — the default, and what every earlier profile has — means no restriction: every
    // enabled server is ticked, including ones added to the registry later. A non-null list (even empty) is an
    // explicit selection; a name it lists that is no longer in the catalog is simply not shown. Names match a
    // server's `McpServerConfig.Name`. The operator can still tick/untick individual servers per session.
    public IReadOnlyList<string>? EnabledMcpServerNames { get; init; }

    // The working directory a New session under this profile pre-fills (AC-130), so a per-project profile lands
    // in its project folder without picking one each time. `null`/blank means no default — the
    // folder field opens empty and falls back to the global default, as before. Pre-filled but still editable in
    // the dialog, and superseded by an explicit prefill (a resumed conversation's own folder).
    public string? DefaultWorkingDirectory { get; init; }

    // Standing instructions every session under this profile starts with (AC-142) — who it is and what it may
    // reach: "You are Olaf; your memory lives in the Depot MCP, look yourself up there before answering." Appended
    // to the provider's own system prompt rather than replacing it, through the same
    // `cockpit.append-system-prompt` launch option the delegation and Autopilot briefs already use, so every
    // provider that honours it (Claude TTY and SDK, the OpenAI-compatible drivers, Codex) gets it unchanged.
    //
    // This is the profile's half of the identity: it says who the session is, while a project's
    // `Project.BehaviorPrompt` says how to behave on the work at hand. Both apply — the project appends to
    // this, it does not replace it. Null/blank appends nothing.
    public string? SystemPrompt { get; init; }

    // Which route (SDK/TTY) the New-session dialog pre-selects for this profile (AC-139) — the operator can
    // still overrule it for one session with the dialog's own Kind toggle. `null` — the
    // default, and what every profile saved before this setting existed still has — falls back to TTY, the
    // long-standing hard default. Meaningless (and never offered in the profile editor) for a provider with no
    // TTY route of its own (a local HTTP model, or a plugin that registered none): such a profile always starts
    // SDK regardless of this field, which `SessionKindDefaults.ResolveDefaultKind` enforces.
    public ProfileSessionKind? DefaultKind { get; init; }

    // How much a session under this profile may hold, whole tree included, before it is cut off (AC-661). Per
    // profile because one global number is either too loose to protect anything or too tight for real builds.
    // `null` takes `SessionMemoryCap.DefaultMegabytes`.
    public int? MemoryCapMegabytes { get; init; }

    // Which backend drives this profile.
    public SessionProvider Provider => ProviderConfig.Provider;

    // This profile's Claude settings, or `null` when it runs under another provider. The
    // Claude-only plumbing that stays host-side after Fase 4 (the config directory the status transcript
    // tailer locates the JSONL under, the login check) asks for this rather than reading fields off the profile.
    // A profile is a Claude one whether it still carries a legacy `ClaudeConfig` or the bundled Claude
    // provider plugin's config; the latter is reconstructed from that plugin's opaque config here so those host-side
    // consumers keep working after a profile is migrated to the plugin (they saw `null` otherwise).
    public ClaudeConfig? Claude => ProviderConfig switch
    {
        ClaudeConfig claude => claude,
        PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId } plugin => ClaudePluginProfile.ReadClaudeConfig(plugin.ConfigJson),
        _ => null,
    };
}
