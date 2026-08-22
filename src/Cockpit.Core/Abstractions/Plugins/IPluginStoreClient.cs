using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Talks to a plugin store (#14, AC-7): fetches its <c>index.json</c>, and downloads a version's zip to a temp
/// file (verifying the checksum when published) from a public, token-authed private, or local
/// <see cref="PluginStoreConfig"/>. The zip then goes to <see cref="IPluginInstaller"/>, never bypassing consent.
/// </summary>
public interface IPluginStoreClient
{
    Task<PluginStoreFetchResult> FetchIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default);

    Task<PluginStoreDownloadResult> DownloadZipAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a store's logo image (#62) — the <c>iconUrl</c> its <c>index.json</c> advertises, absolute or
    /// relative to the store — as raw bytes for the Manage-stores dialog. Http(s) or, for a local store, a file;
    /// capped in size and time. No code and nothing to consent to; a failure is non-fatal, the store simply keeps its emoji/default glyph.
    /// </summary>
    Task<PluginStoreImageResult> DownloadImageAsync(PluginStoreConfig store, string iconUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a workflow template's flow (#69) — a JSON file, not a zip: a template is text, so there is nothing to
    /// unpack, no assembly to load and no consent to running code. The store's checksum is still verified when it
    /// publishes one, so what arrives is what was published.
    /// </summary>
    Task<WorkflowTemplateDownloadResult> DownloadTemplateAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);
}
