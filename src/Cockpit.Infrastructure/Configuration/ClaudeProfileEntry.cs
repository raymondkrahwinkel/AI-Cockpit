using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a single <see cref="ClaudeProfile"/> in the <c>profiles</c> section.</summary>
internal sealed class ClaudeProfileEntry
{
    public string Label { get; set; } = string.Empty;

    public string ConfigDir { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public string? Purpose { get; set; }

    public ProfileDefaultsEntry? Defaults { get; set; }

    public ProviderConfigEntry? Provider { get; set; }

    public static ClaudeProfileEntry FromDomain(ClaudeProfile profile) => new()
    {
        Label = profile.Label,
        ConfigDir = profile.ConfigDir,
        ExecutablePath = profile.ExecutablePath,
        Purpose = profile.Purpose,
        Defaults = profile.Defaults is null ? null : ProfileDefaultsEntry.FromDomain(profile.Defaults),
        Provider = ProviderConfigEntry.FromDomain(profile.ProviderConfig),
    };

    public ClaudeProfile ToDomain() => new(Label, ConfigDir, ExecutablePath, Purpose, Defaults?.ToDomain(), Provider?.ToDomain());
}
