using System.Net.Http.Headers;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Fetches a store's <c>index.json</c> and downloads a version's zip (#14, AC-7). A store is resolved by its
/// <see cref="PluginStoreConfig"/>: a local folder is read from disk; a public remote over a plain GET; a private
/// remote with an <c>Authorization: Bearer</c> header — and a private <c>github.com</c> repo through the
/// authenticated Contents API, since <c>raw.githubusercontent.com</c> will not serve a private repo with a token.
/// A published SHA-256 is verified against the downloaded bytes before the zip is written; a mismatch is rejected.
/// The zip then goes through the normal installer — the store never bypasses consent.
/// </summary>
internal sealed class PluginStoreClient : IPluginStoreClient, ISingletonService
{
    private static readonly HttpClient Http = new();

    /// <summary>A store logo is a small image, not a payload — anything larger is refused rather than downloaded.</summary>
    private const long MaxLogoBytes = 1_048_576;

    /// <summary>One resolved fetch: a local file path or a remote URL, whether to ask GitHub for raw bytes, and the bearer token to send (only ever to the store's own host).</summary>
    private readonly record struct StoreTarget(bool IsLocal, string Value, bool GitHubRaw, string? Token);

    public async Task<PluginStoreFetchResult> FetchIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default)
    {
        if (!_TryResolveIndex(store, out var target, out var error))
        {
            return new PluginStoreFetchResult(false, error, null, null);
        }

        try
        {
            var json = await _ReadStringAsync(target, cancellationToken).ConfigureAwait(false);
            if (!PluginStoreIndex.TryParse(json, out var index, out var parseError))
            {
                return new PluginStoreFetchResult(false, parseError, null, target.Value);
            }

            return new PluginStoreFetchResult(true, null, index, target.Value);
        }
        catch (Exception exception)
        {
            return new PluginStoreFetchResult(false, $"Could not fetch the store index: {exception.Message}", null, target.Value);
        }
    }

    public async Task<PluginStoreDownloadResult> DownloadZipAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        if (!_TryResolveRelative(store, relativePath, out var target, out var error))
        {
            return new PluginStoreDownloadResult(false, error, null);
        }

        try
        {
            var bytes = await _ReadBytesAsync(target, maxBytes: null, cancellationToken).ConfigureAwait(false);
            if (_ChecksumMismatch(bytes, expectedSha256))
            {
                return new PluginStoreDownloadResult(false, "The downloaded plugin did not match the store's published checksum and was rejected.", null);
            }

            var tempPath = Path.Combine(Path.GetTempPath(), "cockpit-store-" + Guid.NewGuid().ToString("N") + ".zip");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

            return new PluginStoreDownloadResult(true, null, tempPath, _UnverifiedWarning(expectedSha256, "plugin"));
        }
        catch (Exception exception)
        {
            return new PluginStoreDownloadResult(false, $"Could not download the plugin: {exception.Message}", null);
        }
    }

    public async Task<WorkflowTemplateDownloadResult> DownloadTemplateAsync(PluginStoreConfig store, string relativePath, string? expectedSha256, CancellationToken cancellationToken = default)
    {
        if (!_TryResolveRelative(store, relativePath, out var target, out var error))
        {
            return new WorkflowTemplateDownloadResult(false, error, null);
        }

        try
        {
            var bytes = await _ReadBytesAsync(target, maxBytes: null, cancellationToken).ConfigureAwait(false);
            if (_ChecksumMismatch(bytes, expectedSha256))
            {
                return new WorkflowTemplateDownloadResult(false, "The downloaded template did not match the store's published checksum and was rejected.", null);
            }

            return new WorkflowTemplateDownloadResult(true, null, System.Text.Encoding.UTF8.GetString(bytes), _UnverifiedWarning(expectedSha256, "template"));
        }
        catch (Exception exception)
        {
            return new WorkflowTemplateDownloadResult(false, $"Could not download the template: {exception.Message}", null);
        }
    }

    public async Task<PluginStoreImageResult> DownloadImageAsync(PluginStoreConfig store, string iconUrl, CancellationToken cancellationToken = default)
    {
        if (!_TryResolveIcon(store, iconUrl, out var target, out var error))
        {
            return new PluginStoreImageResult(false, error, null);
        }

        try
        {
            // A logo must never stall a browse: bound it in time on top of the size cap.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var bytes = await _ReadBytesAsync(target, MaxLogoBytes, timeout.Token).ConfigureAwait(false);

            return new PluginStoreImageResult(true, null, bytes);
        }
        catch (Exception exception)
        {
            return new PluginStoreImageResult(false, $"Could not download the store icon: {exception.Message}", null);
        }
    }

    private static bool _TryResolveIndex(PluginStoreConfig store, out StoreTarget target, out string? error)
    {
        target = default;
        error = null;

        if (store.Kind == PluginStoreKind.Local)
        {
            if (!_TryResolveLocalFile(store.Location, "index.json", out var path, out error))
            {
                return false;
            }

            target = new StoreTarget(IsLocal: true, path, GitHubRaw: false, Token: null);

            return true;
        }

        if (store.HasToken && PluginStoreUrl.TryParseGitHubRepo(store.Location, out var owner, out var repo, out var branch))
        {
            target = new StoreTarget(IsLocal: false, PluginStoreUrl.GitHubContentsUrl(owner, repo, "index.json", branch), GitHubRaw: true, store.Token);

            return true;
        }

        if (!PluginStoreUrl.TryResolveIndexUrl(store.Location, out var indexUrl, out error))
        {
            return false;
        }

        target = new StoreTarget(IsLocal: false, indexUrl, GitHubRaw: false, store.Token);

        return true;
    }

    private static bool _TryResolveRelative(PluginStoreConfig store, string relativePath, out StoreTarget target, out string? error)
    {
        target = default;
        error = null;

        if (store.Kind == PluginStoreKind.Local)
        {
            if (!_TryResolveLocalFile(store.Location, relativePath, out var path, out error))
            {
                return false;
            }

            target = new StoreTarget(IsLocal: true, path, GitHubRaw: false, Token: null);

            return true;
        }

        // A store index may list an absolute http(s) URL for a zip or template (a CDN), the same way it can for an
        // icon. Fetch it as-is rather than resolving it against the store — and only carry the token when it is
        // the store's own origin, so an absolute foreign path never exfiltrates the credential.
        if (Uri.TryCreate(relativePath, UriKind.Absolute, out var absolutePath)
            && (absolutePath.Scheme == Uri.UriSchemeHttp || absolutePath.Scheme == Uri.UriSchemeHttps))
        {
            target = new StoreTarget(IsLocal: false, absolutePath.ToString(), GitHubRaw: false, _TokenForSameOrigin(absolutePath.ToString(), store.Location, store.Token));

            return true;
        }

        if (store.HasToken && PluginStoreUrl.TryParseGitHubRepo(store.Location, out var owner, out var repo, out var branch))
        {
            // The path comes from the store's (untrusted) index — a "../.." must not read another repo through
            // the Contents API with the operator's token.
            if (!PluginStoreUrl.IsSafeRelativePath(relativePath))
            {
                error = "The store index has an unsafe path.";

                return false;
            }

            target = new StoreTarget(IsLocal: false, PluginStoreUrl.GitHubContentsUrl(owner, repo, relativePath, branch), GitHubRaw: true, store.Token);

            return true;
        }

        if (!PluginStoreUrl.TryResolveIndexUrl(store.Location, out var indexUrl, out error))
        {
            return false;
        }

        try
        {
            var url = PluginStoreUrl.ResolveZipUrl(indexUrl, relativePath);

            // The index is store-controlled, and Uri combination lets an absolute or protocol-relative path escape
            // to a foreign host. The token belongs only on the store's own origin, so it is stripped otherwise —
            // the same rule the icon path follows.
            target = new StoreTarget(IsLocal: false, url, GitHubRaw: false, _TokenForSameOrigin(url, indexUrl, store.Token));

            return true;
        }
        catch (Exception exception)
        {
            error = $"The store index has a bad path: {exception.Message}";

            return false;
        }
    }

    /// <summary>Returns the token only when <paramref name="requestUrl"/> is the same origin (scheme, host, port) as the store; a store-index path that resolves to another host gets no credential.</summary>
    private static string? _TokenForSameOrigin(string requestUrl, string storeUrl, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return Uri.TryCreate(requestUrl, UriKind.Absolute, out var request)
            && Uri.TryCreate(storeUrl, UriKind.Absolute, out var origin)
            && string.Equals(request.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            && request.Port == origin.Port
                ? token
                : null;
    }

    private static bool _TryResolveIcon(PluginStoreConfig store, string iconUrl, out StoreTarget target, out string? error)
    {
        target = default;
        error = null;

        // An absolute icon URL is fetched as-is and never carries the store's token — a credential belongs only on
        // a request to the store's own host, not on whatever CDN an index chose to point its logo at. It must be
        // http(s): a file:/ftp:/data: URL is rejected outright rather than handed to the fetcher.
        if (Uri.TryCreate(iconUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
            {
                error = "A store icon must be an http(s) URL.";

                return false;
            }

            target = new StoreTarget(IsLocal: false, absolute.ToString(), GitHubRaw: false, Token: null);

            return true;
        }

        // Otherwise it is relative to the store, and resolves the same way a zip path does.
        return _TryResolveRelative(store, iconUrl, out target, out error);
    }

    private static bool _TryResolveLocalFile(string root, string relativePath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        var rootFull = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;

        // A store's index must not reach outside its own folder — a "../../etc/..." path is rejected, not read.
        if (!string.Equals(candidate, rootFull, StringComparison.Ordinal) && !candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            error = "The store points outside its own folder.";

            return false;
        }

        fullPath = candidate;

        return true;
    }

    private static async Task<string> _ReadStringAsync(StoreTarget target, CancellationToken cancellationToken)
    {
        if (target.IsLocal)
        {
            return await File.ReadAllTextAsync(target.Value, cancellationToken).ConfigureAwait(false);
        }

        using var request = _BuildRequest(target);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> _ReadBytesAsync(StoreTarget target, long? maxBytes, CancellationToken cancellationToken)
    {
        if (target.IsLocal)
        {
            var bytes = await File.ReadAllBytesAsync(target.Value, cancellationToken).ConfigureAwait(false);
            if (maxBytes is { } localCap && bytes.Length > localCap)
            {
                throw new InvalidOperationException("The store icon is too large.");
            }

            return bytes;
        }

        using var request = _BuildRequest(target);
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (maxBytes is { } cap && response.Content.Headers.ContentLength > cap)
        {
            throw new InvalidOperationException("The store icon is too large.");
        }

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (maxBytes is { } limit && payload.Length > limit)
        {
            throw new InvalidOperationException("The store icon is too large.");
        }

        return payload;
    }

    private static HttpRequestMessage _BuildRequest(StoreTarget target)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, target.Value);
        request.Headers.UserAgent.ParseAdd("Cockpit-PluginStore");

        if (target.GitHubRaw)
        {
            // The Contents API returns the file's metadata by default; this asks for the bytes themselves.
            request.Headers.Accept.ParseAdd("application/vnd.github.raw");
        }

        if (!string.IsNullOrWhiteSpace(target.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
        }

        return request;
    }

    private static bool _ChecksumMismatch(byte[] bytes, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return false;
        }

        return !string.Equals(PluginHash.Compute(bytes), expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A non-fatal advisory when the store's index published no per-artifact SHA-256 (AC-46). The store URL is
    /// fully operator-settable and a published hash, when present, rides in the same index as the payload — so the
    /// checksum defends transit, not a compromised store. An index without one leaves the download's integrity
    /// unverifiable. We still allow the install — plenty of simple/local stores publish no hash, and a mismatch on
    /// a published one is already a hard reject — but say so rather than let an unverified artifact land silently.
    /// </summary>
    private static string? _UnverifiedWarning(string? expectedSha256, string artifact) =>
        string.IsNullOrWhiteSpace(expectedSha256)
            ? $"The store published no checksum for this {artifact}, so its integrity could not be verified before install."
            : null;
}
