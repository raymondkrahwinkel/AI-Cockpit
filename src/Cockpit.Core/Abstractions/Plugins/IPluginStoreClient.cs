using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Talks to a plugin store over HTTP (#14): fetches and parses its <c>index.json</c>, and downloads a
/// specific version's zip to a temp file (verifying the store's checksum when one is published). The
/// downloaded zip is then handed to the normal <see cref="IPluginInstaller"/> — the store never bypasses
/// install validation or the consent/hash-pin.
/// </summary>
public interface IPluginStoreClient
{
    Task<PluginStoreFetchResult> FetchIndexAsync(string storeUrl, CancellationToken cancellationToken = default);

    Task<PluginStoreDownloadResult> DownloadZipAsync(string indexUrl, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);
}
