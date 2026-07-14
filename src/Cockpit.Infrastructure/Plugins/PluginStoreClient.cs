using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Fetches a store's <c>index.json</c> and downloads a version's zip over a plain <see cref="HttpClient"/>
/// (#14). The store URL is resolved by <see cref="PluginStoreUrl"/> (GitHub repo / direct json / base dir),
/// and a published SHA-256 is verified against the downloaded bytes before the zip is written — a mismatch
/// is rejected. The zip then goes through the normal installer; the store never bypasses consent.
/// </summary>
internal sealed class PluginStoreClient : IPluginStoreClient, ISingletonService
{
    private static readonly HttpClient Http = new();

    public async Task<PluginStoreFetchResult> FetchIndexAsync(string storeUrl, CancellationToken cancellationToken = default)
    {
        if (!PluginStoreUrl.TryResolveIndexUrl(storeUrl, out var indexUrl, out var urlError))
        {
            return new PluginStoreFetchResult(false, urlError, null, null);
        }

        try
        {
            var json = await _GetStringAsync(indexUrl, cancellationToken).ConfigureAwait(false);
            if (!PluginStoreIndex.TryParse(json, out var index, out var parseError))
            {
                return new PluginStoreFetchResult(false, parseError, null, indexUrl);
            }

            return new PluginStoreFetchResult(true, null, index, indexUrl);
        }
        catch (Exception exception)
        {
            return new PluginStoreFetchResult(false, $"Could not fetch the store index: {exception.Message}", null, indexUrl);
        }
    }

    public async Task<PluginStoreDownloadResult> DownloadZipAsync(string indexUrl, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        string zipUrl;
        try
        {
            zipUrl = PluginStoreUrl.ResolveZipUrl(indexUrl, relativePath);
        }
        catch (Exception exception)
        {
            return new PluginStoreDownloadResult(false, $"The store index has a bad zip path: {exception.Message}", null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, zipUrl);
            request.Headers.UserAgent.ParseAdd("Cockpit-PluginStore");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = PluginHash.Compute(bytes);
                if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return new PluginStoreDownloadResult(false, "The downloaded plugin did not match the store's published checksum and was rejected.", null);
                }
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "cockpit-store-" + Guid.NewGuid().ToString("N") + ".zip");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            return new PluginStoreDownloadResult(true, null, tempPath);
        }
        catch (Exception exception)
        {
            return new PluginStoreDownloadResult(false, $"Could not download the plugin: {exception.Message}", null);
        }
    }

    public async Task<WorkflowTemplateDownloadResult> DownloadTemplateAsync(string indexUrl, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        string url;
        try
        {
            url = PluginStoreUrl.ResolveZipUrl(indexUrl, relativePath);
        }
        catch (Exception exception)
        {
            return new WorkflowTemplateDownloadResult(false, $"The store index has a bad template path: {exception.Message}", null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Cockpit-PluginStore");
            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = PluginHash.Compute(bytes);
                if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return new WorkflowTemplateDownloadResult(false, "The downloaded template did not match the store's published checksum and was rejected.", null);
                }
            }

            return new WorkflowTemplateDownloadResult(true, null, System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (Exception exception)
        {
            return new WorkflowTemplateDownloadResult(false, $"Could not download the template: {exception.Message}", null);
        }
    }

    private static async Task<string> _GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cockpit-PluginStore");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
