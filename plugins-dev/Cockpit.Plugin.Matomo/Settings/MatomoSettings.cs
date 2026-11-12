using Cockpit.Plugin.Matomo.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Matomo.Settings;

internal sealed class MatomoSettings(IPluginStorage storage)
{
    public bool McpEnabled => storage.Get<bool?>(MatomoKeys.McpEnabled) ?? false;

    public string Url => (storage.Get<string>(MatomoKeys.Url) ?? string.Empty).Trim();

    public string AuthToken => storage.GetSecret(MatomoKeys.AuthToken) ?? string.Empty;

    public IReadOnlyList<MatomoSite> OfferedSites => storage.Get<List<MatomoSite>>(MatomoKeys.OfferedSites) ?? [];

    public int? DefaultSiteId => storage.Get<int?>(MatomoKeys.DefaultSite);

    public bool IsConfigured => Url.Length > 0 && AuthToken.Length > 0;
}
