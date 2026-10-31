namespace Cockpit.Core.ModelIntelligence;

public enum ModelObservationKind
{
    QuotaReading,
    RateLimitRefusal,
    Latency,
}

// AC-1982: Profile is the provider's opaque key for the subscription/profile a quota reading belongs to; null when unknown.
// AC-1812: one thing seen about a model. Value is the used percent for a quota reading, 1 for a refusal, seconds for a latency.
public sealed record ModelObservation(string Provider, string? Model, ModelObservationKind Kind, DateTimeOffset At, double Value, string? Window = null, string? Profile = null);

// The only two defaults the derivation has; everything else is read from the data.
public static class ModelObservationDefaults
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(56);

    public const int MinimumSamples = 10;
}

public interface IModelObservationStore
{
    void Record(ModelObservation observation);

    IReadOnlyList<ModelObservation> Read();
}
