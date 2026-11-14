using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

public interface IPluginUiPackages
{
    /// <summary>
    /// Reads the enabled server plugin's verified UI load closure at exactly the requested version and pin.
    /// </summary>
    Task<PluginUiPackage> GetUiPackageAsync(string folderId, string version, string sha256, CancellationToken cancellationToken = default);
}
