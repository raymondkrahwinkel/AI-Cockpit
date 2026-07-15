using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a single <see cref="SessionProfile"/> in the <c>profiles</c> section.
/// <para>
/// <see cref="ConfigDir"/> and <see cref="ExecutablePath"/> stay where they have always been — at the top of the
/// entry, not inside the provider block. They are Claude's settings and the domain now models them that way
/// (<see cref="ClaudeConfig"/>), but moving them on disk would rewrite every operator's config to gain nothing:
/// the mapping below absorbs the difference, which is what a mapping is for. They move when Claude becomes a
/// plugin and its settings become that plugin's config — one shape change, at the point where the shape actually
/// changes.
/// </para>
/// </summary>
internal sealed class SessionProfileEntry
{
    public string Label { get; set; } = string.Empty;

    /// <summary>Claude's config directory. Read for a Claude profile, ignored for any other.</summary>
    public string ConfigDir { get; set; } = string.Empty;

    /// <summary>Claude's executable override. Read for a Claude profile, ignored for any other.</summary>
    public string? ExecutablePath { get; set; }

    public string? Purpose { get; set; }

    public ProfileDefaultsEntry? Defaults { get; set; }

    /// <summary>
    /// Which provider this profile runs under. Written for every profile, including Claude's — but an entry
    /// without one is still read as Claude, because that is what an older cockpit wrote and an operator's config
    /// is not a thing to invalidate. A profile saved by this version says so explicitly; one saved by an earlier
    /// version is understood, and says so explicitly the next time it is saved.
    /// </summary>
    public ProviderConfigEntry? Provider { get; set; }

    /// <summary>What this profile allows when another session delegates to it (#67); absent means it is not a target.</summary>
    public DelegationPolicyEntry? Delegation { get; set; }

    /// <summary>A ceiling on the session CLI's memory, in MB. Absent — the normal case — means no ceiling: a capped session that needs more memory dies rather than slows.</summary>
    public int? MemoryLimitMb { get; set; }

    public static SessionProfileEntry FromDomain(SessionProfile profile) => new()
    {
        Label = profile.Label,
        ConfigDir = profile.Claude?.ConfigDir ?? string.Empty,
        ExecutablePath = profile.Claude?.ExecutablePath,
        Purpose = profile.Purpose,
        Defaults = profile.Defaults is null ? null : ProfileDefaultsEntry.FromDomain(profile.Defaults),
        Provider = ProviderConfigEntry.FromDomain(profile.ProviderConfig),
        Delegation = DelegationPolicyEntry.FromDomain(profile.Delegation),
        MemoryLimitMb = profile.MemoryLimitMb,
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

        return new(Label, providerConfig, Purpose, defaults, Delegation?.ToDomain(), MemoryLimitMb);
    }
}
