namespace Cockpit.Core.Profiles;

// A distinct identity a session runs under: provider plus its settings, so several identities (two Claude logins,
// a local Ollama model, a plugin-backed provider) run side by side without mixing state. `ProviderConfig` is
// required (#26) — no longer nullable-meaning-Claude — so a different provider is always a new profile, not a mutation.
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
    // `null`/empty means only the inherited environment. Host-controlled keys (`TtyEnvironment.IsHostControlled` —
    // nested-agent markers, Anthropic credentials) never win: spawn paths drop them so a profile can't reintroduce what the host strips.
    public IReadOnlyList<ProfileEnvironmentVariable>? EnvironmentVariables { get; init; }

    // The MCP servers a New session under this profile pre-selects (AC-130), so a project profile need not re-toggle
    // them every time. `null` (default, every earlier profile) means no restriction — every enabled server ticked,
    // including future ones; a non-null list (even empty) is an explicit selection, matched by `McpServerConfig.Name`.
    public IReadOnlyList<string>? EnabledMcpServerNames { get; init; }

    // The working directory a New session under this profile pre-fills (AC-130), so a per-project profile lands in its
    // project folder without picking one each time. `null`/blank falls back to the global default; the field stays
    // editable, and is superseded by an explicit prefill such as a resumed conversation's own folder.
    public string? DefaultWorkingDirectory { get; init; }

    // Standing instructions every session under this profile starts with (AC-142) — who it is and what it may reach,
    // e.g. "You are Olaf; your memory lives in Depot MCP." Appended to the provider's system prompt via the same
    // `cockpit.append-system-prompt` option Autopilot/delegation use; a project's `BehaviorPrompt` appends further on top.
    public string? SystemPrompt { get; init; }

    // AC-1071: which assistant/persona a session under this profile runs as, e.g. "Zyra" — its own field rather
    // than a sentence buried in `SystemPrompt`, so the cockpit can state the choice and cancel the "which brain?"
    // question deterministically. Machine-local: a profile never travels with a shared project.
    public string? Assistant { get; init; }

    // Which route (SDK/TTY) the New-session dialog pre-selects (AC-139); the operator can still override per session.
    // `null` (every pre-existing profile) falls back to TTY. Meaningless for a provider with no TTY route (local HTTP
    // model, or a plugin registering none) — such a profile always starts SDK, enforced by `SessionKindDefaults.ResolveDefaultKind`.
    public ProfileSessionKind? DefaultKind { get; init; }

    // How much a session under this profile may hold, whole tree included, before it is cut off (AC-661). Per
    // profile because one global number is either too loose to protect anything or too tight for real builds.
    // `null` takes `SessionMemoryCap.DefaultMegabytes`.
    public int? MemoryCapMegabytes { get; init; }

    // Which backend drives this profile.
    public SessionProvider Provider => ProviderConfig.Provider;

    // This profile's Claude settings, or `null` under another provider. Host-side Claude plumbing (status tailer,
    // login check) asks for this rather than reading profile fields directly — works whether the profile still carries
    // a legacy `ClaudeConfig` or the bundled plugin's config, reconstructed here so migrated profiles don't see `null`.
    public ClaudeConfig? Claude => ProviderConfig switch
    {
        ClaudeConfig claude => claude,
        PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId } plugin => ClaudePluginProfile.ReadClaudeConfig(plugin.ConfigJson),
        _ => null,
    };
}
