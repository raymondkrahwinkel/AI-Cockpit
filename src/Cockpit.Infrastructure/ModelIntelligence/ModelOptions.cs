using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Core.ModelIntelligence;

namespace Cockpit.Infrastructure.ModelIntelligence;

internal static class ModelOptions
{
    internal static async Task<string> ReadAsync(
        ModelPictureReader modelPicture,
        string? taskType = null,
        decimal? maxPricePerMTokens = null,
        string? provider = null,
        string? profile = null,
        IReadOnlySet<string>? allowedProfiles = null,
        IReadOnlyDictionary<string, object>? sessions = null)
    {
        try
        {
            var picture = await modelPicture.ReadAsync().ConfigureAwait(false);
            var requestedKind = taskType?.Trim().ToLowerInvariant() switch { "coding" => "coding", "agentic" => "agentic", _ => "intelligence" };
            var rows = picture.Rows
                .Where(row => allowedProfiles is null || allowedProfiles.Contains(row.Profile))
                .Where(row => maxPricePerMTokens is not { } max || row.PriceNow is null
                    || ((row.PriceNow.Input ?? 0m) <= max && (row.PriceNow.Output ?? 0m) <= max))
                .Where(row => string.IsNullOrEmpty(provider) || row.Provider.Contains(provider, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.IsNullOrEmpty(profile) || string.Equals(row.Profile, profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var kind = _RankingKind(rows, requestedKind);
            var comparableRows = rows.Where(row => _Index(row, kind) is not null)
                .OrderByDescending(row => _Index(row, kind))
                .ToArray();
            var nonComparableRows = rows.Where(row => _Index(row, kind) is null).ToArray();
            var rankedRows = comparableRows.Concat(nonComparableRows).ToArray();

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

            var models = rankedRows.Select(row =>
            {
                var index = _Index(row, kind);
                var images = ImageInput.Resolve(row.ImageInputMode, row.HasImageTransport, row.ServiceImageInput?.At(picture.At), row.OpenRouterImageInput?.At(picture.At));
                return new
                {
                    profile = row.Profile,
                    provider = row.Provider,
                    model = row.Model,
                    sessions = sessions?.GetValueOrDefault(row.Profile),
                    publicModelRef = row.PublicModelRef,
                    family = _Family(row.PublicModelRef),
                    comparable = index is not null,
                    supportsImageInput = images.SupportsVision,
                    imageInputMode = row.ImageInputMode.ToString(),
                    imageInputSource = images.Source,
                    serviceImageInput = row.ServiceImageInput,
                    openRouterImageInput = row.OpenRouterImageInput,
                    price = row.SelfHosted ? (object)"self-hosted / own endpoint" : row.Price is null ? Unknown(row.Unmatched ?? $"OpenRouter lists no price for {row.PublicModelRef}") : new { inputPerMTokens = row.Price.Input, outputPerMTokens = row.Price.Output, fact = Fact(row.Price.Source) },
                    priceNow = row.SelfHosted ? "self-hosted / own endpoint" : row.PriceNow is null ? (object)"unknown" : new { inputPerMTokens = row.PriceNow.Input, outputPerMTokens = row.PriceNow.Output, applied = row.PriceNow.Applied },
                    modifiers = row.Modifiers.Select(modifier => new { modifier.Condition, modifier.Factor, modifier.ActiveNow }),
                    quality = index is null ? Unknown(row.Unmatched ?? $"not comparable: OpenRouter carries no {kind} index for {row.PublicModelRef}") : new { kind, index, basis = kind, fact = Fact(row.PublicSource) },
                    scores = row.Indices is null ? null : new
                    {
                        coding = _Score(row.Indices.Coding, row.PublicSource, Fact),
                        agentic = _Score(row.Indices.Agentic, row.PublicSource, Fact),
                        intelligence = _Score(row.Indices.Intelligence, row.PublicSource, Fact),
                    },
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

            var message = kind != requestedKind
                ? $"Requested {requestedKind}, but not every row has it; ranked by {kind}."
                : comparableRows.Length == rows.Length
                ? $"Ranked by {kind} score."
                : $"Ranked by {kind} score; {nonComparableRows.Length} row(s) are not comparable because they have no {kind} score.";
            return JsonSerializer.Serialize(new { ok = true, taskType = requestedKind, at = picture.At, count = models.Length, ranking = new { kind, message, comparableCount = comparableRows.Length, notComparableCount = nonComparableRows.Length }, models, attribution = used });
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new { ok = false, error = exception.Message });
        }
    }

    private static decimal? _Index(ModelPictureRow row, string kind) => row.Indices is null ? null : kind switch
    {
        "coding" => row.Indices.Coding,
        "agentic" => row.Indices.Agentic,
        _ => row.Indices.Intelligence,
    };

    private static string _RankingKind(IReadOnlyList<ModelPictureRow> rows, string requestedKind) =>
        rows.All(row => _Index(row, requestedKind) is not null)
            ? requestedKind
            : new[] { "coding", "agentic", "intelligence" }
                .OrderByDescending(kind => rows.Count(row => _Index(row, kind) is not null))
                .ThenBy(kind => kind == requestedKind ? 0 : 1)
                .First();

    private static object? _Score(decimal? value, Sourced? source, Func<Sourced?, object?> fact) =>
        value is null ? null : new { index = value, fact = fact(source) };

    private static object _Family(string? publicModelRef)
    {
        var reference = publicModelRef?.Split(" via ", StringSplitOptions.None)[0];
        var match = reference is null ? null : Regex.Match(reference, "^(?<provider>[^/]+)/(?<model>[^/]+)$");
        if (match is null || !match.Success)
        {
            return new { provider = "unknown", generation = "unknown", tier = "unknown" };
        }

        var model = match.Groups["model"].Value;
        var generation = Regex.Match(model, "\\d+(?:\\.\\d+)*");
        var before = generation.Success ? model[..generation.Index].Trim('-') : string.Empty;
        var after = generation.Success ? model[(generation.Index + generation.Length)..].Trim('-') : string.Empty;
        var tier = after.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? before.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return new
        {
            provider = match.Groups["provider"].Value,
            generation = generation.Success ? generation.Value : "unknown",
            tier = tier is "gpt" or "claude" or null ? "unknown" : tier,
        };
    }
}

