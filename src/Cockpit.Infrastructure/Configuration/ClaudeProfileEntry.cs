using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a single <see cref="ClaudeProfile"/> in the <c>profiles</c> section.</summary>
internal sealed class ClaudeProfileEntry
{
    public string Label { get; set; } = string.Empty;

    public string ConfigDir { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public string? Purpose { get; set; }

    public static ClaudeProfileEntry FromDomain(ClaudeProfile profile) => new()
    {
        Label = profile.Label,
        ConfigDir = profile.ConfigDir,
        ExecutablePath = profile.ExecutablePath,
        Purpose = profile.Purpose,
    };

    public ClaudeProfile ToDomain() => new(Label, ConfigDir, ExecutablePath, Purpose);
}
