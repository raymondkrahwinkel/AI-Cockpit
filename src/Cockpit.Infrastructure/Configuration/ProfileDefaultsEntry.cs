using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a profile's <see cref="ProfileDefaults"/> nested in a <see cref="ClaudeProfileEntry"/>.</summary>
internal sealed class ProfileDefaultsEntry
{
    public string PermissionMode { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Effort { get; set; } = string.Empty;

    public static ProfileDefaultsEntry FromDomain(ProfileDefaults defaults) => new()
    {
        PermissionMode = defaults.PermissionMode,
        Model = defaults.Model,
        Effort = defaults.Effort,
    };

    public ProfileDefaults ToDomain() => new(PermissionMode, Model, Effort);
}
