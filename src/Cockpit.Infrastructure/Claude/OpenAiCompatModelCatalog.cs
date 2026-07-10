using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Claude;

namespace Cockpit.Infrastructure.Claude;

/// <summary>
/// <see cref="IModelCatalog"/> over a local server's OpenAI-compatible <c>GET /v1/models</c> (works for
/// both Ollama and LM Studio). Failures (server down, timeout, malformed body) map to an empty list so the
/// Manage-profiles model picker simply shows no suggestions rather than surfacing an error.
/// </summary>
internal sealed class OpenAiCompatModelCatalog(HttpClient httpClient, ILogger<OpenAiCompatModelCatalog> logger)
    : IModelCatalog, ISingletonService
{
    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ModelListResponse>(cancellationToken).ConfigureAwait(false);
            return body?.Data?
                .Select(entry => entry.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Could not list models from {BaseUrl}", baseUrl);
            return [];
        }
    }

    private sealed class ModelListResponse
    {
        [JsonPropertyName("data")]
        public List<ModelEntry>? Data { get; set; }
    }

    private sealed class ModelEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}
