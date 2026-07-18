using Cockpit.Core.Plugins;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Talks to a plugin store (#14, AC-7): fetches and parses its <c>index.json</c>, and downloads a specific
/// version's zip to a temp file (verifying the store's checksum when one is published). A store is a
/// <see cref="PluginStoreConfig"/> — a public remote, a private remote reached with a bearer token, or a local
/// folder — and the client resolves each the right way. The downloaded zip is then handed to the normal
/// <see cref="IPluginInstaller"/>: the store never bypasses install validation or the consent/hash-pin.
/// </summary>
public interface IPluginStoreClient
{
    Task<PluginStoreFetchResult> FetchIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default);

    Task<PluginStoreDownloadResult> DownloadZipAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a store's logo image (#62) — the <c>iconUrl</c> its <c>index.json</c> advertises, absolute or
    /// relative to the store — as raw bytes for the Manage-stores dialog to show. Http(s) or, for a local store, a
    /// file; capped in size and time. There is no code and nothing to consent to, and a failure is non-fatal: the
    /// store simply keeps its emoji/default glyph.
    /// </summary>
    Task<PluginStoreImageResult> DownloadImageAsync(PluginStoreConfig store, string iconUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a workflow template's flow (#69) — a JSON file, not a zip: a template is text, so there is nothing to
    /// unpack, no assembly to load and no consent to running code. The store's checksum is still verified when it
    /// publishes one, so what arrives is what was published.
    /// </summary>
    Task<WorkflowTemplateDownloadResult> DownloadTemplateAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default);
}
