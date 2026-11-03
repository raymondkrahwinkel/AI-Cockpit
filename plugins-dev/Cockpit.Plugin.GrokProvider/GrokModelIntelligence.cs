using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

internal static class GrokModelIntelligence
{
    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static async Task<PluginModelIntelligence> ReadAsync(string configJson, CancellationToken cancellationToken)
    {
        var config = JsonSerializer.Deserialize<OpenAiCompatConfig>(configJson, OpenAiCompatConfig.JsonOptions);
        if (config is null || string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            return new();
        }

        var key = OpenAiCompatConfig.ResolveApiKey(config, OpenAiCompatConfig.ApiKeyEnvVar, OpenAiCompatDefaultBaseUrls.Grok);
        if (string.IsNullOrWhiteSpace(key))
        {
            return new();
        }

        var endpoint = config.BaseUrl.TrimEnd('/');
        var scope = endpoint + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            Cache.TryGetValue(scope, out var cached);
            if (cached is not null && now < cached.ExpiresAt)
            {
                if (string.IsNullOrWhiteSpace(config.Model) || cached.Value.Models.Any(model => model.Model == config.Model) || cached.Missing.Contains(config.Model))
                {
                    return cached.Value;
                }

                cached.Missing.Add(config.Model);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/language-models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            try
            {
                using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var listing = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken).ConfigureAwait(false);
                var asOf = DateTimeOffset.UtcNow;
                var source = new PluginFactSource("xAI language-models", asOf, Lifetime);
                var models = new List<PluginModelCostEstimate>();
                foreach (var model in listing?.Models ?? [])
                {
                    if (model.InputModalities is null)
                    {
                        continue;
                    }

                    var supports = model.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase);
                    foreach (var id in new[] { model.Id }.Concat(model.Aliases ?? []))
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            models.Add(new PluginModelCostEstimate(id) { SupportsImageInput = supports, ImageInputSource = source });
                        }
                    }
                }

                var value = new PluginModelIntelligence { Models = models };
                var missing = cached is not null && now < cached.ExpiresAt ? cached.Missing : new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(config.Model) && !models.Any(model => model.Model == config.Model))
                {
                    missing.Add(config.Model);
                }
                Cache[scope] = new(value, asOf + Lifetime, missing);
                return value;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                var value = new PluginModelIntelligence();
                Cache[scope] = new(value, DateTimeOffset.UtcNow.AddSeconds(30), new HashSet<string>(StringComparer.Ordinal) { config.Model });
                return value;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed record CacheEntry(PluginModelIntelligence Value, DateTimeOffset ExpiresAt, HashSet<string> Missing);
    private sealed class ModelList
    {
        [JsonPropertyName("models")]
        public List<Model>? Models { get; set; }
    }

    private sealed class Model
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }
        [JsonPropertyName("input_modalities")]
        public List<string>? InputModalities { get; set; }
    }
}
