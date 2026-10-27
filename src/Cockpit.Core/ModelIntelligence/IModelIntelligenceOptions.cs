namespace Cockpit.Core.ModelIntelligence;

// A public data source the model facts come from, named wherever its data is shown (its terms ask for credit).
public sealed record ModelDataSource(string Name, string Url, string Text);

public static class ModelDataAttribution
{
    public static readonly IReadOnlyList<ModelDataSource> Sources =
    [
        new("OpenRouter", "https://openrouter.ai", "Model prices, context sizes and capabilities come from the OpenRouter models API."),
        new("Artificial Analysis", "https://artificialanalysis.ai", "Intelligence, coding and agentic indices are Artificial Analysis benchmarks, relayed by OpenRouter."),
    ];

    // The one line About shows.
    public static string Line => "Model data: " + string.Join(" · ", Sources.Select(source => source.Name));
}

// How old the facts from one source are; null when that source has not delivered any yet.
public sealed record ModelDataAge(string Source, DateTimeOffset? AsOf);

public sealed record ModelIntelligenceOverview(
    IReadOnlyList<ModelDataAge> Sources,
    IReadOnlyList<LocalModelEntry> LocalModels,
    IReadOnlyList<DerivedPattern> Patterns);

// What Options › Model intelligence shows and changes.
public interface IModelIntelligenceOptions
{
    Task<ModelIntelligenceOverview> ReadAsync(CancellationToken cancellationToken = default);

    // Fetches the public data again now; false when the source did not answer and the last known data stays.
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    Task SaveLocalModelsAsync(IReadOnlyList<LocalModelEntry> localModels, CancellationToken cancellationToken = default);
}
