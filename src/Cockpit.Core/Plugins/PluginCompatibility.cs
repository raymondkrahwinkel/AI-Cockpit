using Cockpit.Core.Configuration;

namespace Cockpit.Core.Plugins;

// AC-181: Same contract-major/minHostVersion gate as PluginLoadPolicy, applied to a catalogue version
// instead of an on-disk manifest, shared by the store's "Incompatible" badge and the provisioning
// refusal so both agree. (Omitted: the specific callers named; see ticket for detail.)
public static class PluginCompatibility
{
    public static bool IsCompatible(PluginStoreVersion version, int hostAbstractionsMajor, Version hostVersion) =>
        (version.AbstractionsVersion is not { } abstractionsVersion || abstractionsVersion == hostAbstractionsMajor)
        && PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, hostVersion);

    // Why this host cannot run `version`, or null when it can.
    public static string? IncompatibilityReason(PluginStoreVersion version, int hostAbstractionsMajor, Version hostVersion)
    {
        if (IsCompatible(version, hostAbstractionsMajor, hostVersion))
        {
            return null;
        }

        return version.AbstractionsVersion is { } abstractionsVersion && abstractionsVersion != hostAbstractionsMajor
            ? $"Built for plugin contract version {abstractionsVersion}, this cockpit provides {hostAbstractionsMajor}"
            : $"Requires {CockpitProduct.DisplayName} {version.MinHostVersion} or later";
    }
}
