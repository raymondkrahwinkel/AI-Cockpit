using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cockpit.Core.ModelIntelligence;

namespace Cockpit.Infrastructure.ModelIntelligence;

internal sealed class OpenRouterModelIntelligence : IModelIntelligence
{
    private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
    private const string Source = "OpenRouter /api/v1/models";
    // AC-1855: v2 drops caches keyed by canonical_slug, which no publicModelRef ever matched.
    private const string CacheFileName = "openrouter-models.v2.json";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly string _cachePath;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OpenRouterModelIntelligence(string cacheRoot, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cachePath = Path.Combine(cacheRoot, "model-intelligence", CacheFileName);
        _handler = handler;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ModelSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var cached = await _ReadCacheAsync(cancellationToken);
            if (cached is not null && _clock.GetUtcNow() - cached.FetchedAt < RefreshInterval)
            {
                return cached.Snapshots;
            }

            try
            {
                var fresh = await _FetchAsync(cancellationToken);
                await _WriteCacheAsync(fresh, cancellationToken);
                return fresh.Snapshots;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                return cached?.Snapshots ?? [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _WriteCacheAsync(await _FetchAsync(cancellationToken), cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<_Cache> _FetchAsync(CancellationToken cancellationToken)
    {
        using var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Cockpit", "1.0"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (payload.Length > MaxResponseBytes)
        {
            throw new IOException("OpenRouter model response exceeded the configured size limit.");
        }

        using var document = JsonDocument.Parse(payload);
        var fetchedAt = _clock.GetUtcNow();
        var entries = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(model => !(_String(model, "id")?.EndsWith(":batch", StringComparison.Ordinal) ?? false)).ToArray()
            : [];
        var byId = entries
            .Select(model => _ParseSnapshot(model, fetchedAt))
            .OfType<ModelSnapshot>()
            .GroupBy(model => model.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // An alias such as ~anthropic/claude-opus-latest answers with the facts of the model it points at today.
        foreach (var entry in entries)
        {
            if (_String(entry, "id") is { } alias && entry.TryGetProperty("alias_target", out var target) && _String(target, "slug") is { } slug && byId.TryGetValue(slug, out var targetSnapshot))
            {
                byId[alias] = targetSnapshot with { Id = alias };
            }
        }

        return new _Cache(fetchedAt, [.. byId.Values]);
    }

    private static ModelSnapshot? _ParseSnapshot(JsonElement model, DateTimeOffset fetchedAt)
    {
        var sourceId = _String(model, "id");
        if (sourceId is null)
        {
            return null;
        }

        var pricing = model.TryGetProperty("pricing", out var pricingElement) && pricingElement.ValueKind == JsonValueKind.Object
            ? pricingElement
            : default;
        var price = _Price(pricing, new ModelPrice(null, null));
        var overrides = pricing.ValueKind == JsonValueKind.Object && pricing.TryGetProperty("overrides", out var overridesElement) && overridesElement.ValueKind == JsonValueKind.Array
            ? overridesElement.EnumerateArray().ToArray()
            : [];
        var tiers = overrides
            .Where(overridePrice => _Long(overridePrice, "min_prompt_tokens") is not null)
            .Select(overridePrice => new ModelPriceTier(_Long(overridePrice, "min_prompt_tokens")!.Value, _Price(overridePrice, price)))
            .ToArray();
        var windows = overrides
            .Where(overridePrice => _Time(overridePrice, "utc_start") is not null || _Time(overridePrice, "utc_end") is not null || _Days(overridePrice).Count > 0)
            .Select(overridePrice => new PriceWindow(_Days(overridePrice), _Time(overridePrice, "utc_start"), _Time(overridePrice, "utc_end"), _Price(overridePrice, price)))
            .ToArray();
        bool? supportsTools = model.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array
            ? parameters.EnumerateArray().Any(parameter => string.Equals(parameter.GetString(), "tools", StringComparison.Ordinal))
            : null;
        var indices = model.TryGetProperty("benchmarks", out var benchmarks) && benchmarks.ValueKind == JsonValueKind.Object && benchmarks.TryGetProperty("artificial_analysis", out var artificialAnalysis)
            ? new ModelIndices(_Decimal(artificialAnalysis, "intelligence_index"), _Decimal(artificialAnalysis, "coding_index"), _Decimal(artificialAnalysis, "agentic_index"))
            : new ModelIndices(null, null, null);
        return new ModelSnapshot(
            sourceId,
            _String(model, "name") ?? sourceId,
            price,
            _Long(model, "context_length"),
            supportsTools,
            indices,
            tiers,
            windows,
            Source,
            fetchedAt,
            RefreshInterval);
    }

    private async Task<_Cache?> _ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            return await JsonSerializer.DeserializeAsync<_Cache>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task _WriteCacheAsync(_Cache cache, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporaryPath = _cachePath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, cache, cancellationToken: cancellationToken);
        }

        File.Move(temporaryPath, _cachePath, overwrite: true);
    }

    private static ModelPrice _Price(JsonElement value, ModelPrice fallback) => new(
        _PriceDecimal(value, "prompt") ?? fallback.PromptPerMillion,
        _PriceDecimal(value, "completion") ?? fallback.CompletionPerMillion);

    private static string? _String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;

    // Prices arrive as strings, the Artificial Analysis indices as numbers.
    private static decimal? _Decimal(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.Number && child.TryGetDecimal(out var number) ? number
        : _String(value, property) is { } text && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static decimal? _PriceDecimal(JsonElement value, string property) => _Decimal(value, property) * 1_000_000;

    private static long? _Long(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var child) && child.TryGetInt64(out var result) ? result : null;

    private static TimeOnly? _Time(JsonElement value, string property)
    {
        var hhmm = _Long(value, property);
        return hhmm is null || hhmm is < 0 or > 2359 || hhmm % 100 > 59 ? null : new TimeOnly((int)(hhmm / 100), (int)(hhmm % 100));
    }

    private static IReadOnlyList<DayOfWeek> _Days(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("utc_days", out var days) && days.ValueKind == JsonValueKind.Array
            ? days.EnumerateArray().Select(day => Enum.TryParse<DayOfWeek>(day.GetString(), ignoreCase: true, out var result) ? result : (DayOfWeek?)null).Where(day => day is not null).Select(day => day!.Value).ToArray()
            : [];

    private sealed record _Cache(DateTimeOffset FetchedAt, IReadOnlyList<ModelSnapshot> Snapshots);
}
