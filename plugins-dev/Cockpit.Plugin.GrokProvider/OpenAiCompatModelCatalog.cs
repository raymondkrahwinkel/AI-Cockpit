using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.GrokProvider;

// AC-929: the models a profile's own base URL currently offers, over the OpenAI-compatible `GET /models`.
// Mirrors Cockpit.Plugin.GeminiProvider's own copy (AC-926) — plugins cannot share code with each other.
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
