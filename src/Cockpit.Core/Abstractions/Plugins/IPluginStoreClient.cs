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

    /// <summary>
    /// Fetches a store's logo image (#62) — the <c>iconUrl</c> its <c>index.json</c> advertises, absolute or
    /// relative to the index — as raw bytes for the Manage-stores dialog to show. Http(s) only, capped in size
    /// and time; there is no code and nothing to consent to, and a failure is non-fatal: the store simply keeps
    /// its emoji/default glyph.
    /// </summary>
    Task<PluginStoreImageResult> DownloadImageAsync(string indexUrl, string iconUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a workflow template's flow (#69) — a JSON file, not a zip: a template is text, so there is nothing to
    /// unpack, no assembly to load and no consent to running code. The store's checksum is still verified when it
    /// publishes one, so what arrives is what was published.
    /// </summary>
    Task<WorkflowTemplateDownloadResult> DownloadTemplateAsync(string indexUrl, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);
}
