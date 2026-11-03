using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

internal sealed class LocalModelImageFacts
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Generations = new(StringComparer.Ordinal);
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(30);

    public static void Invalidate(SessionProfile profile)
    {
        if (profile.ProviderConfig is not (LmStudioConfig or OllamaConfig)) return;
        Generations.AddOrUpdate(profile.Label, 1, (_, generation) => unchecked(generation + 1));
        if (profile.ProviderConfig is OllamaConfig ollama) OllamaModelMetadata.Invalidate(ollama.BaseUrl);
    }

    public async Task<PluginModelIntelligence?> ReadAsync(string profile, ProviderConfig config, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, config.GetType()))));
            var generation = Generations.GetValueOrDefault(profile);
            if (_entries.TryGetValue(profile, out var cached) && cached.Fingerprint == fingerprint && cached.Generation == generation && cached.Expires > DateTimeOffset.UtcNow)
            {
                return cached.Reading;
            }

            PluginModelIntelligence? reading = null;
            var complete = false;
            try
            {
                (reading, complete) = config switch
                {
                    LmStudioConfig lm => await _ReadLmAsync(lm, cancellationToken).ConfigureAwait(false),
                    OllamaConfig ollama => await _ReadOllamaAsync(ollama, cancellationToken).ConfigureAwait(false),
                    _ => (null, false),
                };
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            _entries[profile] = new(fingerprint, generation, reading, DateTimeOffset.UtcNow.Add(complete ? SuccessLifetime : FailureLifetime));
            return reading;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<(PluginModelIntelligence?, bool)> _ReadLmAsync(LmStudioConfig config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl.TrimEnd('/')}/api/v1/models");
        if (!string.IsNullOrEmpty(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            return await _ReadLmV0Async(config, cancellationToken).ConfigureAwait(false);
        }
        response.EnsureSuccessStatusCode();
        using var document = await _ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            return await _ReadLmV0Async(config, cancellationToken).ConfigureAwait(false);
        }
        var source = new PluginFactSource("LM Studio /api/v1/models", DateTimeOffset.UtcNow, SuccessLifetime);
        var facts = new List<PluginModelCostEstimate>();
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object
                || !capabilities.TryGetProperty("vision", out var vision) || vision.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                continue;
            }
            var id = key.GetString();
            if (id is null) continue;
            facts.Add(new(id) { SupportsImageInput = !vision.GetBoolean(), ImageInputSource = source });
            if (item.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array)
            {
                foreach (var instance in instances.EnumerateArray())
                {
                    if (instance.ValueKind == JsonValueKind.Object && instance.TryGetProperty("id", out var instanceId) && instanceId.ValueKind == JsonValueKind.String && instanceId.GetString() is { } alias && alias != id)
                    {
                        facts.Add(new(alias) { SupportsImageInput = vision.GetBoolean(), ImageInputSource = source });
                    }
                }
            }
        }
        if (facts.Count == 0) return await _ReadLmV0Async(config, cancellationToken).ConfigureAwait(false);
        return (new PluginModelIntelligence { Models = facts }, true);
    }

    private async Task<(PluginModelIntelligence?, bool)> _ReadLmV0Async(LmStudioConfig config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl.TrimEnd('/')}/api/v0/models");
        if (!string.IsNullOrEmpty(config.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await _ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (null, false);
        var source = new PluginFactSource("LM Studio /api/v0/models", DateTimeOffset.UtcNow, SuccessLifetime);
        var facts = new List<PluginModelCostEstimate>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() is { } name && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                facts.Add(new(name) { SupportsImageInput = type.GetString() == "vlm", ImageInputSource = source });
            }
        }
        return (new PluginModelIntelligence { Models = facts }, facts.Count > 0);
    }

    private async Task<(PluginModelIntelligence?, bool)> _ReadOllamaAsync(OllamaConfig config, CancellationToken cancellationToken)
    {
        var baseUrl = config.BaseUrl.TrimEnd('/');
        using var tags = await _http.GetAsync($"{baseUrl}/api/tags", cancellationToken).ConfigureAwait(false);
        tags.EnsureSuccessStatusCode();
        using var document = await _ReadJsonAsync(tags, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return (null, false);
        var names = new HashSet<string>(StringComparer.Ordinal) { config.Model };
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { } value) names.Add(value);
        }
        var results = await Task.WhenAll(names.Select(name => _ReadOllamaModelAsync(baseUrl, name, cancellationToken))).ConfigureAwait(false);
        var facts = results.OfType<PluginModelCostEstimate>().ToArray();
        return (new PluginModelIntelligence { Models = facts }, facts.Length == names.Count);
    }

    private async Task<PluginModelCostEstimate?> _ReadOllamaModelAsync(string baseUrl, string name, CancellationToken cancellationToken)
    {
        var show = await OllamaModelMetadata.ReadAsync(_http, baseUrl, name, cancellationToken).ConfigureAwait(false);
        if (show is null || show.Value.Data.ValueKind != JsonValueKind.Object || !show.Value.Data.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array) return null;
        if (capabilities.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return null;
        var vision = capabilities.EnumerateArray().Any(value => value.GetString() == "vision");
        return new(name) { SupportsImageInput = vision, ImageInputSource = new("Ollama /api/show", show.Value.AsOf, SuccessLifetime) };
    }

    private static async Task<JsonDocument> _ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);

    private sealed record Entry(string Fingerprint, int Generation, PluginModelIntelligence? Reading, DateTimeOffset Expires);
}
