using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// `IModelCatalog` over a local server's OpenAI-compatible `GET /v1/models` (works for
// both Ollama and LM Studio). Failures (server down, timeout, malformed body) map to an empty list so the
// Manage-profiles model picker simply shows no suggestions rather than surfacing an error.
internal sealed class OpenAiCompatModelCatalog(HttpClient httpClient, ILogger<OpenAiCompatModelCatalog> logger)
    : IModelCatalog, ISingletonService
{
    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = await _ReadModelEntriesAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
            return _ModelIds(entries);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning(ex, "Could not list models.");
            return [];
        }
    }

    public async Task<IReadOnlyList<string>?> ReadModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _ReadModelEntriesAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (entries is null)
        {
            return null;
        }

        var ids = _ModelIds(entries);
        return ids.Count == entries.Count ? ids : null;
    }

    private static IReadOnlyList<string> _ModelIds(List<ModelEntry?>? entries) => entries?
        .Select(entry => entry?.Id)
        .OfType<string>()
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .ToList() ?? [];

    private async Task<List<ModelEntry?>?> _ReadModelEntriesAsync(string baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        if (!_HasHttpBaseUrl(baseUrl))
        {
            return null;
        }

        try
        {
            using var request = _Request(baseUrl, apiKey);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ModelListResponse>(cancellationToken).ConfigureAwait(false);
            return body?.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not list models.");
            return null;
        }
    }

    public async Task<bool> ProbeAsync(string baseUrl, string? apiKey, TimeSpan timeout)
    {
        if (!_HasHttpBaseUrl(baseUrl))
        {
            return false;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            using var request = _Request(baseUrl, apiKey);
            using var response = await httpClient.SendAsync(request, cancellation.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not probe models.");
            return false;
        }
    }

    private static bool _HasHttpBaseUrl(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static HttpRequestMessage _Request(string baseUrl, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private sealed class ModelListResponse
    {
        [JsonPropertyName("data")]
        public List<ModelEntry?>? Data { get; set; }
    }

    private sealed class ModelEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}
