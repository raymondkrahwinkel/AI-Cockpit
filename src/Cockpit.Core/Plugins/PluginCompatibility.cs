using Cockpit.Core.Configuration;

namespace Cockpit.Core.Plugins;

// Whether this host can run a specific store-advertised `PluginStoreVersion` (AC-181): the same
// contract-major and `minHostVersion` gate as `PluginLoadPolicy`, applied to a version still in
// the catalogue rather than an on-disk manifest. Shared by the store dialog's pre-download "Incompatible" badge
// (`StorePluginRowViewModel.IsIncompatible`) and `PluginProvisioningService`'s pre-download
// refusal, so the two can never disagree about the same version.
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
