using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of a profile's <see cref="ProfileDefaults"/> nested in a <see cref="SessionProfileEntry"/>.</summary>
internal sealed class ProfileDefaultsEntry
{
    public string PermissionMode { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Effort { get; set; } = string.Empty;

    public bool AutoApproveTools { get; set; }

    /// <summary>Per-profile defaults for the provider plugin's declared launch options, keyed by option key; absent means none.</summary>
    public Dictionary<string, string>? OptionDefaults { get; set; }

    public static ProfileDefaultsEntry FromDomain(ProfileDefaults defaults) => new()
    {
        PermissionMode = defaults.PermissionMode,
        Model = defaults.Model,
        Effort = defaults.Effort,
        AutoApproveTools = defaults.AutoApproveTools,
        OptionDefaults = defaults.OptionDefaults is { Count: > 0 } options ? new Dictionary<string, string>(options) : null,
    };

    public ProfileDefaults ToDomain() => new(PermissionMode, Model, Effort, AutoApproveTools)
    {
        OptionDefaults = OptionDefaults is { Count: > 0 } options ? new Dictionary<string, string>(options) : null,
    };
}
