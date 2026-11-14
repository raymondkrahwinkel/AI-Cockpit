using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

internal sealed partial class RemotePluginAdministration : IPluginUiPackages
{
    public Task<PluginUiPackage> GetUiPackageAsync(string folderId, string version, string sha256, CancellationToken cancellationToken = default) =>
        client.SendAsync<PluginUiPackage>(HttpMethod.Get,
            $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/ui/package?version={Uri.EscapeDataString(version)}&sha256={Uri.EscapeDataString(sha256)}",
            null, ConnectKeyEndpoints.Json, cancellationToken);
}
