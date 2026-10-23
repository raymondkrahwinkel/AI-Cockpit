using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot;

internal enum DepotApiOutcome
{
    Success,
    NotSignedIn,
    Forbidden,
    Conflict,
    TooLarge,
    OlderDepot,
    NotModified,
    Failed,
}

internal sealed record DepotApiResult(DepotApiOutcome Outcome, ReadOnlyMemory<byte> Content, string? ETag)
{
    // AC-1633: remove this one compatibility decision when AC-1642 drops support for older Depots.
    public bool ShouldFallbackToMcp => Outcome == DepotApiOutcome.OlderDepot;
}

internal sealed class DepotApi : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public DepotApi(ICockpitHost host, DepotConnectionRegistration connection, HttpClient? httpClient = null)
    {
        _baseUrl = connection.Url.TrimEnd('/');
        _http = httpClient ?? new HttpClient(host.CreateAuthorizedHandler(connection.McpServerName));
        _ownsHttp = httpClient is null;
    }

    public async Task<DepotApiResult> GetAsync(string path, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _Url(path));
        if (etag is { Length: > 0 })
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<DepotApiResult> GetAsync(string path, CancellationToken cancellationToken) =>
        GetAsync(path, etag: null, cancellationToken);

    public Task<DepotApiResult> PostJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        _SendJsonAsync(HttpMethod.Post, path, value, baseChecksum: null, guardWrite: false, cancellationToken);

    public async Task<DepotApiResult> PostAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _Url(path));
        return await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DepotApiResult> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, _Url(path));
        return await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<DepotApiResult> PutJsonAsync<T>(
        string path, T value, string? baseChecksum, CancellationToken cancellationToken) =>
        _SendJsonAsync(HttpMethod.Put, path, value, baseChecksum, guardWrite: true, cancellationToken);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<DepotApiResult> _SendJsonAsync<T>(
        HttpMethod method, string path, T value, string? baseChecksum, bool guardWrite,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _Url(path))
        {
            Content = JsonContent.Create(value, options: DepotChannel.Json),
        };
        if (guardWrite && baseChecksum is { Length: > 0 })
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{baseChecksum}\""));
        }
        else if (guardWrite)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }

        return await _SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DepotApiResult> _SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var outcome = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => DepotApiOutcome.NotSignedIn,
            HttpStatusCode.Forbidden => DepotApiOutcome.Forbidden,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => DepotApiOutcome.Conflict,
            HttpStatusCode.RequestEntityTooLarge => DepotApiOutcome.TooLarge,
            HttpStatusCode.NotFound => DepotApiOutcome.OlderDepot,
            HttpStatusCode.NotModified => DepotApiOutcome.NotModified,
            _ when response.IsSuccessStatusCode => DepotApiOutcome.Success,
            _ => DepotApiOutcome.Failed,
        };
        var content = outcome == DepotApiOutcome.Success
            ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)
            : [];
        return new DepotApiResult(outcome, content, response.Headers.ETag?.Tag);
    }

    private string _Url(string path) => $"{_baseUrl}/{path.TrimStart('/')}";
}
