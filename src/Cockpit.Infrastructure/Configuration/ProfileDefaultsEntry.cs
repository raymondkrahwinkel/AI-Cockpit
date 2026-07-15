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

    // Reads the obsolete legacy typed fields on purpose — this is the persistence half of the one-time migration, one
    // of the two places allowed to touch them (see ProfileDefaults). A plugin profile writes them blank (its start
    // defaults live in OptionDefaults), so they carry nothing forward.
#pragma warning disable CS0618 // legacy Claude-CLI defaults, migration/persistence only
    public static ProfileDefaultsEntry FromDomain(ProfileDefaults defaults) => new()
    {
        PermissionMode = defaults.PermissionMode,
        Model = defaults.Model,
        Effort = defaults.Effort,
        AutoApproveTools = defaults.AutoApproveTools,
        OptionDefaults = defaults.OptionDefaults is { Count: > 0 } options ? new Dictionary<string, string>(options) : null,
    };
#pragma warning restore CS0618

    public ProfileDefaults ToDomain() => new(PermissionMode, Model, Effort, AutoApproveTools)
    {
        OptionDefaults = OptionDefaults is { Count: > 0 } options ? new Dictionary<string, string>(options) : null,
    };
}
