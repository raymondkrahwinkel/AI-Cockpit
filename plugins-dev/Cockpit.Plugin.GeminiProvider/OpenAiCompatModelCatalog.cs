using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.GeminiProvider;

// AC-926: the models a profile's own base URL currently offers, over the OpenAI-compatible `GET /models`.
// The base URL already carries the provider's version segment (`/v1`, `/v1beta/openai/`), so only the
// resource is appended here — unlike the host's local-server catalog, which inserts `/v1` itself.
internal static class OpenAiCompatModelCatalog
{
    public static async Task<IReadOnlyList<string>> ListAsync(HttpClient httpClient, string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var listing = await response.Content.ReadFromJsonAsync<ModelListResponse>(cancellationToken).ConfigureAwait(false);
        return listing?.Data?
            .Select(entry => entry.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    // Only the ids are read, so a gateway that omits the rest of OpenAI's model shape still lists.
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
