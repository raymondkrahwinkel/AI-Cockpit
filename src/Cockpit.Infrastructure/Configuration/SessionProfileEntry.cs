using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of a single `SessionProfile`. `ConfigDir`/`ExecutablePath` stay at the top of the entry,
// not inside the provider block — moving them on disk would rewrite every config to gain nothing; the
// mapping below absorbs the difference until Claude becomes a plugin and the shape actually changes.
internal sealed class SessionProfileEntry
{
    public string Label { get; set; } = string.Empty;

    // Claude's config directory. Read for a Claude profile, ignored for any other.
    public string ConfigDir { get; set; } = string.Empty;

    // Claude's executable override. Read for a Claude profile, ignored for any other.
    public string? ExecutablePath { get; set; }

    public string? Purpose { get; set; }

    public ProfileDefaultsEntry? Defaults { get; set; }

    // Which provider this profile runs under, written for every profile. An entry without one still
    // reads as Claude — what an older cockpit wrote — and gains the explicit field on its next save.
    public ProviderConfigEntry? Provider { get; set; }

    // What this profile allows when another session delegates to it (#67); absent means it is not a target.
    public DelegationPolicyEntry? Delegation { get; set; }

    // The profile's spawn environment variables (AC-22); absent means none.
    public List<ProfileEnvironmentVariableEntry>? EnvironmentVariables { get; set; }

    // The MCP servers a New session under this profile pre-selects (AC-130); absent means no restriction (all enabled servers). An explicit list — even empty — is the operator's chosen set.
    public List<string>? EnabledMcpServers { get; set; }

    // The working directory a New session under this profile pre-fills (AC-130); absent/blank means no default.
    public string? DefaultWorkingDirectory { get; set; }

    // Standing instructions every session under this profile starts with (AC-142); absent/blank appends nothing.
    public string? SystemPrompt { get; set; }

    // The New-session Kind toggle's pre-selection for this profile (AC-139); absent means TTY, the long-standing hard default (and what every profile saved before this setting existed still gets).
    public string? DefaultKind { get; set; }

    // How much memory a session under this profile may hold, whole tree included (AC-661); absent means the app default.
    public int? MemoryCapMegabytes { get; set; }

    public static SessionProfileEntry FromDomain(SessionProfile profile) => new()
    {
        Label = profile.Label,
        // Only a legacy in-tree ClaudeConfig profile writes settings to the top-level fields; a plugin
        // profile keeps them in its own PluginConfigJson, so these stay blank and nothing is duplicated.
        ConfigDir = (profile.ProviderConfig as ClaudeConfig)?.ConfigDir ?? string.Empty,
        ExecutablePath = (profile.ProviderConfig as ClaudeConfig)?.ExecutablePath,
        Purpose = profile.Purpose,
        Defaults = profile.Defaults is null ? null : ProfileDefaultsEntry.FromDomain(profile.Defaults),
        Provider = ProviderConfigEntry.FromDomain(profile.ProviderConfig),
        Delegation = DelegationPolicyEntry.FromDomain(profile.Delegation),
        EnvironmentVariables = profile.EnvironmentVariables is { Count: > 0 } variables
            ? [.. variables.Select(ProfileEnvironmentVariableEntry.FromDomain)]
            : null,
        // A null restriction stays null (no section written); an explicit selection is persisted verbatim, empty
        // list included — "these none" is a real choice, distinct from "no restriction".
        EnabledMcpServers = profile.EnabledMcpServerNames is { } names ? [.. names] : null,
        DefaultWorkingDirectory = string.IsNullOrWhiteSpace(profile.DefaultWorkingDirectory) ? null : profile.DefaultWorkingDirectory,
        SystemPrompt = string.IsNullOrWhiteSpace(profile.SystemPrompt) ? null : profile.SystemPrompt,
        DefaultKind = profile.DefaultKind?.ToString(),
        MemoryCapMegabytes = profile.MemoryCapMegabytes,
    };

    public SessionProfile ToDomain()
    {
        var providerConfig = Provider?.ToDomain(ConfigDir, ExecutablePath) ?? ClaudePluginProfile.Create(ConfigDir, ExecutablePath);
        var defaults = Defaults?.ToDomain();

        // A Claude profile migrated to the plugin carries its typed permission/model/effort defaults into the generic
        // OptionDefaults, so the migrated profile keeps its saved start settings — the profile-edit and New-session
        // dialogs read those generically now. Idempotent: an already-migrated profile with OptionDefaults is untouched.
        if (defaults is not null && providerConfig is PluginProviderConfig { ProviderId: ClaudePluginProfile.ProviderId })
        {
            defaults = ClaudePluginProfile.WithMigratedOptionDefaults(defaults);
        }

        return new(Label, providerConfig, Purpose, defaults, Delegation?.ToDomain())
        {
            EnvironmentVariables = EnvironmentVariables is { Count: > 0 }
                ? [.. EnvironmentVariables.Select(entry => entry.ToDomain())]
                : null,
            EnabledMcpServerNames = EnabledMcpServers is { } names ? [.. names] : null,
            DefaultWorkingDirectory = string.IsNullOrWhiteSpace(DefaultWorkingDirectory) ? null : DefaultWorkingDirectory,
            SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
            // An absent/unrecognised value reads as "no default" (null) rather than throwing, the same tolerance
            // WorkspacePaneEntry gives PaneSessionKind — an older cockpit.json (or a hand-edited value) never fails
            // to load over this, it just falls back to the TTY default SessionKindDefaults.ResolveDefaultKind applies.
            DefaultKind = Enum.TryParse<ProfileSessionKind>(DefaultKind, ignoreCase: true, out var parsedDefaultKind)
                ? parsedDefaultKind
                : null,
            MemoryCapMegabytes = MemoryCapMegabytes,
        };
    }
}
