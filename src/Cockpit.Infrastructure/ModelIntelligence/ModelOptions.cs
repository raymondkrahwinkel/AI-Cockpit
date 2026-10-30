using System.Text.Json;

namespace Cockpit.Infrastructure.ModelIntelligence;

internal static class ModelOptions
{
    internal static async Task<string> ReadAsync(
        ModelPictureReader modelPicture,
        string? taskType = null,
        decimal? maxPricePerMTokens = null,
        string? provider = null,
        string? profile = null,
        IReadOnlySet<string>? allowedProfiles = null)
    {
        try
        {
            var picture = await modelPicture.ReadAsync().ConfigureAwait(false);
            var kind = taskType?.Trim().ToLowerInvariant() switch { "coding" => "coding", "agentic" => "agentic", _ => "intelligence" };
            (decimal? Value, string? Basis) Index(ModelPictureRow row) => row.Indices is null ? (null, null) : kind switch
            {
                "coding" when row.Indices.Coding is { } value => (value, "coding"),
                "coding" when row.Indices.Agentic is { } value => (value, "coding missing → agentic"),
                "coding" when row.Indices.Intelligence is { } value => (value, "coding missing → intelligence"),
                "agentic" when row.Indices.Agentic is { } value => (value, "agentic"),
                "agentic" when row.Indices.Coding is { } value => (value, "agentic missing → coding"),
                "agentic" when row.Indices.Intelligence is { } value => (value, "agentic missing → intelligence"),
                _ when row.Indices.Intelligence is { } value => (value, "intelligence"),
                _ => (null, null),
            };

            var rows = picture.Rows
                .Where(row => allowedProfiles is null || allowedProfiles.Contains(row.Profile))
                .Where(row => maxPricePerMTokens is not { } max || row.PriceNow is null
                    || ((row.PriceNow.Input ?? 0m) <= max && (row.PriceNow.Output ?? 0m) <= max))
                .OrderBy(row => Index(row).Value is null)
                .ThenByDescending(row => Index(row).Value ?? 0m)
                .ToArray();

            var used = new SortedSet<string>(StringComparer.Ordinal);
            object? Fact(Sourced? source)
            {
                if (source is null)
                {
                    return null;
                }

                used.Add(source.Name);
                return new
                {
                    source = source.Name,
                    asOf = source.AsOf,
                    ageSeconds = (long)Math.Max(0, (picture.At - source.AsOf).TotalSeconds),
                    stale = picture.At - source.AsOf > source.ValidFor,
                };
            }

            static object Unknown(string why) => $"unknown: {why}";

            var models = rows.Select(row =>
            {
                var index = Index(row);
                return new
                {
                    profile = row.Profile,
                    provider = row.Provider,
                    model = row.Model,
                    publicModelRef = row.PublicModelRef,
                    price = row.SelfHosted ? (object)"self-hosted / own endpoint" : row.Price is null ? Unknown(row.Unmatched ?? $"OpenRouter lists no price for {row.PublicModelRef}") : new { inputPerMTokens = row.Price.Input, outputPerMTokens = row.Price.Output, fact = Fact(row.Price.Source) },
                    priceNow = row.SelfHosted ? "self-hosted / own endpoint" : row.PriceNow is null ? (object)"unknown" : new { inputPerMTokens = row.PriceNow.Input, outputPerMTokens = row.PriceNow.Output, applied = row.PriceNow.Applied },
                    modifiers = row.Modifiers.Select(modifier => new { modifier.Condition, modifier.Factor, modifier.ActiveNow }),
                    quality = index.Value is null ? Unknown(row.Unmatched ?? $"OpenRouter carries no Artificial Analysis {kind} index for {row.PublicModelRef}") : new { kind, index = index.Value, basis = index.Basis, fact = Fact(row.PublicSource) },
                    contextTokens = row.Context is null ? Unknown(row.Unmatched ?? $"OpenRouter lists no context length for {row.PublicModelRef}") : new { tokens = row.Context.Tokens, fact = Fact(row.Context.Source) },
                    limits = row.Limits.Count == 0
                        ? (object)"unknown"
                        : row.Limits.Select(limit => new { limit.Label, limit.Value, limit.Unit, limit.Model, fact = Fact(Sourced.From(limit.Source)) }).ToArray(),
                    peakWindows = row.PeakWindows.Select(window => new
                    {
                        origin = window.Origin,
                        kind = window.Kind,
                        start = window.Start.ToString("HH:mm"),
                        end = window.End.ToString("HH:mm"),
                        timeZone = window.TimeZoneId,
                        days = window.Days.Select(day => day.ToString()),
                        activeNow = window.ActiveNow,
                        fact = Fact(window.Source),
                    }).ToArray(),
                    ownMeasurements = row.Derived.Count == 0
                        ? (object)"unknown"
                        : row.Derived.Select(derived => new
                        {
                            metric = derived.Pattern.Metric.ToString(),
                            timeZone = derived.Pattern.TimeZoneId,
                            n = derived.Pattern.N,
                            confidence = Math.Round(derived.Pattern.Confidence, 2),
                            period = new { from = derived.Pattern.From, to = derived.Pattern.To },
                            peaks = derived.Pattern.Peaks.Count == 0
                                ? (object)"none stands out"
                                : derived.Pattern.Peaks.Select(peak => new
                                {
                                    days = peak.Days.Select(day => day.ToString()),
                                    start = peak.Start.ToString("HH:mm"),
                                    end = peak.End.ToString("HH:mm"),
                                    value = Math.Round(peak.Value, 3),
                                    baseline = Math.Round(peak.Baseline, 3),
                                    n = peak.N,
                                }).ToArray(),
                            fact = Fact(derived.Source),
                        }).ToArray(),
                    quota = row.Quota.Count == 0
                        ? (object)"unknown"
                        : row.Quota.Select(state => new
                        {
                            window = state.Window.Label,
                            usedPercent = state.Window.UsedPercent,
                            resetsAt = state.Window.ResetsAt,
                            windowMinutes = state.Window.WindowMinutes,
                            fact = Fact(Sourced.From(state.Source)),
                        }).ToArray(),
                };
            }).ToArray();

            models = models
                .Where(model => string.IsNullOrEmpty(provider) || model.provider.Contains(provider, StringComparison.OrdinalIgnoreCase))
                .Where(model => string.IsNullOrEmpty(profile) || string.Equals(model.profile, profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return JsonSerializer.Serialize(new { ok = true, taskType = kind, at = picture.At, count = models.Length, models, attribution = used });
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = exception.Message });
        }
    }
}

