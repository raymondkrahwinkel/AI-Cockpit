using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace Cockpit.Infrastructure.Sessions;

internal static class OllamaModelMetadata
{
    private static readonly ConcurrentDictionary<(string Endpoint, string Model), Entry> Entries = new();

    public static void Invalidate(string baseUrl)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/api/show";
        foreach (var key in Entries.Keys)
        {
            if (key.Endpoint == endpoint) Entries.TryRemove(key, out _);
        }
    }

    public static async Task<Result?> ReadAsync(HttpClient http, string baseUrl, string model, CancellationToken cancellationToken)
    {
        var endpoint = $"{baseUrl.TrimEnd('/')}/api/show";
        var entry = Entries.GetOrAdd((endpoint, model), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Expires > DateTimeOffset.UtcNow) return entry.Value;
            Result? value = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await http.PostAsJsonAsync(endpoint, new { model }, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), cancellationToken: timeout.Token).ConfigureAwait(false);
                value = new(document.RootElement.Clone(), DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            entry.Value = value;
            var hasCapabilities = value is { Data.ValueKind: JsonValueKind.Object } result
                && result.Data.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array;
            entry.Expires = DateTimeOffset.UtcNow.Add(hasCapabilities ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));
            return value;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Result? Value { get; set; }
        public DateTimeOffset Expires { get; set; }
    }

    internal readonly record struct Result(JsonElement Data, DateTimeOffset AsOf);
}
