using System.Runtime.InteropServices;
using Cockpit.Core.Configuration;

namespace Cockpit.Core.Plugins;

// AC-181: Same contract-major/minHostVersion gate as PluginLoadPolicy, applied to a catalogue version
// instead of an on-disk manifest, shared by the store's "Incompatible" badge and the provisioning
// refusal so both agree. AC-1740 adds the platform: a version with packages but none for this RID.
public static class PluginCompatibility
{
    public static bool IsCompatible(PluginStoreVersion version, int hostAbstractionsMajor, Version hostVersion, string? runtimeIdentifier = null) =>
        (version.AbstractionsVersion is not { } abstractionsVersion || abstractionsVersion == hostAbstractionsMajor)
        && PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, hostVersion)
        && version.PackageFor(runtimeIdentifier) is not null;

    // Why this host cannot run `version`, or null when it can.
    public static string? IncompatibilityReason(PluginStoreVersion version, int hostAbstractionsMajor, Version hostVersion, string? runtimeIdentifier = null)
    {
        if (IsCompatible(version, hostAbstractionsMajor, hostVersion, runtimeIdentifier))
        {
            return null;
        }

        if (version.AbstractionsVersion is { } abstractionsVersion && abstractionsVersion != hostAbstractionsMajor)
        {
            return $"Built for plugin contract version {abstractionsVersion}, this cockpit provides {hostAbstractionsMajor}";
        }

        return PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, hostVersion)
            ? $"No package for {runtimeIdentifier ?? RuntimeInformation.RuntimeIdentifier}"
            : $"Requires {CockpitProduct.DisplayName} {version.MinHostVersion} or later";
    }
}
