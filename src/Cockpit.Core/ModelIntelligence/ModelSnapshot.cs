namespace Cockpit.Core.ModelIntelligence;

public sealed record ModelSnapshot(
    string Id,
    string Name,
    ModelPrice Price,
    long? ContextTokens,
    bool? SupportsTools,
    ModelIndices Indices,
    IReadOnlyList<ModelPriceTier> PromptPriceTiers,
    IReadOnlyList<PriceWindow> PriceWindows,
    string Source,
    DateTimeOffset FetchedAt,
    TimeSpan TimeToLive)
{
    public ModelPrice PriceAt(DateTimeOffset utc) => PriceWindows.FirstOrDefault(window => window.IsActiveAt(utc))?.Price ?? Price;
}

public sealed record ModelPrice(decimal? PromptPerMillion, decimal? CompletionPerMillion);

public sealed record ModelPriceTier(long MinimumPromptTokens, ModelPrice Price);

public sealed record ModelIndices(decimal? Intelligence, decimal? Coding, decimal? Agentic);

public sealed record PriceWindow(IReadOnlyList<DayOfWeek> Days, TimeOnly? StartsAtUtc, TimeOnly? EndsAtUtc, ModelPrice Price)
{
    public bool IsActiveAt(DateTimeOffset utc)
    {
        if (Days.Count > 0 && !Days.Contains(utc.UtcDateTime.DayOfWeek))
        {
            return false;
        }

        if (StartsAtUtc is null || EndsAtUtc is null)
        {
            return StartsAtUtc is null && EndsAtUtc is null;
        }

        var time = TimeOnly.FromDateTime(utc.UtcDateTime);
        return StartsAtUtc < EndsAtUtc
            ? time >= StartsAtUtc && time < EndsAtUtc
            : StartsAtUtc > EndsAtUtc
                ? time >= StartsAtUtc || time < EndsAtUtc
                : true;
    }
}
