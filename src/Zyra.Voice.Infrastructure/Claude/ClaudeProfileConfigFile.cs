using Zyra.Voice.Core.Profiles;

namespace Zyra.Voice.Infrastructure.Claude;

/// <summary>
/// JSON shape persisted under the <c>profiles</c> section of <c>zyra-voice.json</c>.
/// Kept as a plain DTO separate from <see cref="ClaudeProfile"/> so the on-disk shape can
/// evolve independently of the domain record.
/// </summary>
internal sealed class ClaudeProfileConfigFile
{
    public List<ClaudeProfileEntry> Profiles { get; set; } = [];
}

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
