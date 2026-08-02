using Cockpit.Core.Configuration;

namespace Cockpit.Core.Plugins;

/// <summary>
/// Whether this host can run a specific store-advertised <see cref="PluginStoreVersion"/> (AC-181): the same
/// contract-major and <c>minHostVersion</c> gate as <see cref="PluginLoadPolicy"/>, applied to a version still in
/// the catalogue rather than an on-disk manifest. Shared by the store dialog's pre-download "Incompatible" badge
/// (<c>StorePluginRowViewModel.IsIncompatible</c>) and <see cref="PluginProvisioningService"/>'s pre-download
/// refusal, so the two can never disagree about the same version.
/// </summary>
public static class PluginCompatibility
{
    public static bool IsCompatible(PluginStoreVersion version, int hostAbstractionsMajor, Version hostVersion) =>
        (version.AbstractionsVersion is not { } abstractionsVersion || abstractionsVersion == hostAbstractionsMajor)
        && PluginLoadPolicy.MeetsMinHostVersion(version.MinHostVersion, hostVersion);

    /// <summary>Why this host cannot run <paramref name="version"/>, or null when it can.</summary>
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
