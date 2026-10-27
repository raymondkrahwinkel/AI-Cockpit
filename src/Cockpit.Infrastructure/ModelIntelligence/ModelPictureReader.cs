using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.ModelIntelligence;

// AC-1813: what the assistant is told about each configured profile's model — the public snapshot (T1) overlaid by the
// provider's own facts (T2-T5), joined through PublicModelRef. It only states facts; every one carries its source and age.
internal sealed class ModelPictureReader(
    IModelIntelligence publicData,
    IProviderModelIntelligence providers,
    ISessionProfileStore profiles,
    TimeProvider? clock = null,
    IModelObservationStore? observations = null,
    TimeZoneInfo? zone = null) : ISingletonService
{
    // Quota moves minute by minute, and the assistant asks before every spawn: one provider read per minute is enough.
    internal static readonly TimeSpan ProviderCacheFor = TimeSpan.FromSeconds(60);

    // A provider that does not answer in time is "unknown" for this call; it never holds the others up.
    internal static readonly TimeSpan ProviderBudget = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PluginModelIntelligence? Reading)> _cache = new(StringComparer.Ordinal);

    public async Task<ModelPicture> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var snapshotsTask = publicData.GetSnapshotsAsync(cancellationToken);
        var known = await profiles.LoadAsync(cancellationToken).ConfigureAwait(false);
        var readings = await Task.WhenAll(known.Select(profile => _ReadProviderAsync(profile.Label, now, cancellationToken))).ConfigureAwait(false);
        var snapshots = (await snapshotsTask.ConfigureAwait(false)).ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);

        // A profile that pins a model is one row; a plugin profile (model chosen per option) is one row per model its provider reports.
        var derived = observations is null ? [] : ModelPatterns.Derive(observations.Read(), zone ?? TimeZoneInfo.Local);
        var rows = known.SelectMany((profile, index) => _ModelsOf(profile, readings[index]).Select(model => _Row(profile, model, readings[index], snapshots, derived, now))).ToArray();
        return new ModelPicture(now, rows);
    }

    private async Task<PluginModelIntelligence?> _ReadProviderAsync(string label, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(label, out var cached) && now - cached.At < ProviderCacheFor)
        {
            return cached.Reading;
        }

        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(ProviderBudget);
            var reading = await providers.ReadAsync(label, budget.Token).ConfigureAwait(false);
            _cache[label] = (now, reading);
            return reading;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One provider failing or timing out is "unknown" for that profile only.
            return null;
        }
    }

    private static string?[] _ModelsOf(SessionProfile profile, PluginModelIntelligence? reading) =>
        ProfileModel.Of(profile) is { } pinned ? [pinned] : reading?.Models.Count > 0 ? [.. reading.Models.Select(estimate => estimate.Model)] : [null];

    private static ModelPictureRow _Row(
        SessionProfile profile,
        string? model,
        PluginModelIntelligence? reading,
        IReadOnlyDictionary<string, ModelSnapshot> snapshots,
        IReadOnlyList<DerivedPattern> derived,
        DateTimeOffset now)
    {
        var estimate = model is null ? null : reading?.Models.FirstOrDefault(candidate => string.Equals(candidate.Model, model, StringComparison.Ordinal));
        var publicRef = estimate?.PublicModelRef ?? model;
        var snapshot = publicRef is not null && snapshots.TryGetValue(publicRef, out var found) ? found : null;
        var providerName = profile.ProviderConfig is PluginProviderConfig plugin ? plugin.ProviderId : profile.Provider.ToString();

        var publicSource = snapshot is null ? null : new Sourced(snapshot.Source, snapshot.FetchedAt, snapshot.TimeToLive);

        // A provider's own price wins over the public one for the same model; its source says which one you got.
        ModelPriceFact? price = null;
        if (profile.ProviderConfig is LmStudioConfig or OllamaConfig)
        {
            // A model on this machine costs nothing per token; the public cloud price of the same model is not what it costs here.
            price = new ModelPriceFact(0m, 0m, new Sourced("local model on this machine", now, TimeSpan.MaxValue));
        }
        else if (estimate is { Source: { } estimateSource } && (estimate.EstimatedInputUsdPerMillionTokens is not null || estimate.EstimatedOutputUsdPerMillionTokens is not null))
        {
            price = new ModelPriceFact(estimate.EstimatedInputUsdPerMillionTokens, estimate.EstimatedOutputUsdPerMillionTokens, Sourced.From(estimateSource));
        }
        else if (snapshot is not null && (snapshot.Price.PromptPerMillion is not null || snapshot.Price.CompletionPerMillion is not null))
        {
            price = new ModelPriceFact(snapshot.Price.PromptPerMillion, snapshot.Price.CompletionPerMillion, publicSource!);
        }

        var modifiers = estimate?.PriceModifiers ?? [];
        ModelPriceNow? priceNow = null;
        if (price is not null)
        {
            var active = modifiers.Where(modifier => modifier.Window is { } window && _IsActive(window, now)).ToArray();
            var publicWindow = price.Source == publicSource && publicSource is not null ? snapshot!.PriceWindows.FirstOrDefault(window => window.IsActiveAt(now)) : null;
            var factor = active.Aggregate(1m, (product, modifier) => product * modifier.Factor);
            priceNow = new ModelPriceNow(
                (publicWindow?.Price.PromptPerMillion ?? price.Input) * factor,
                (publicWindow?.Price.CompletionPerMillion ?? price.Output) * factor,
                [.. active.Select(modifier => modifier.Condition), .. publicWindow is null ? [] : new[] { "public price window" }]);
        }

        var quality = snapshot is null ? null : snapshot.Indices;
        var windows = new List<ModelPeakWindow>();
        windows.AddRange((reading?.Windows ?? []).Select(window => new ModelPeakWindow("provider", window.Kind.ToString(), window.Start, window.End, window.TimeZoneId, window.Days, _IsActive(window, now), Sourced.From(window.Source))));
        windows.AddRange((snapshot?.PriceWindows ?? []).Select(window => new ModelPeakWindow("public", "PriceWindow", window.StartsAtUtc ?? TimeOnly.MinValue, window.EndsAtUtc ?? TimeOnly.MinValue, "Etc/UTC", window.Days, window.IsActiveAt(now), publicSource!)));

        // A provider that names a model reports its ceilings for that model; account-wide ones (Model null) hold for all.
        var limits = (reading?.Limits ?? []).Where(limit => limit.Model is null || limit.Model == model).ToArray();

        // Our own measurements of this model; a pattern without a model speaks for the whole provider.
        var own = derived
            .Where(pattern => string.Equals(pattern.Provider, providerName, StringComparison.OrdinalIgnoreCase) && (pattern.Model is null || pattern.Model == model))
            .Select(pattern => new ModelDerivedFact(pattern, new Sourced("own measurement", pattern.To, ModelObservationDefaults.Retention)))
            .ToArray();

        return new ModelPictureRow(
            profile.Label,
            providerName,
            model,
            publicRef,
            price,
            priceNow,
            [.. modifiers.Select(modifier => new ModelModifier(modifier.Condition, modifier.Factor, modifier.Window is null ? null : _IsActive(modifier.Window, now)))],
            quality,
            snapshot?.ContextTokens is { } context && publicSource is not null ? new ModelContextFact(context, publicSource) : null,
            [.. limits],
            windows,
            [.. (reading?.Quota ?? [])],
            publicSource,
            own);
    }

    // Wall-clock window in its own IANA zone; one running past midnight belongs to the day it opened.
    private static bool _IsActive(PluginTimeWindow window, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeOnly.FromDateTime(local.DateTime);
        bool OpensOn(DayOfWeek day) => window.Days.Count == 0 || window.Days.Contains(day);
        if (window.End > window.Start)
        {
            return time >= window.Start && time < window.End && OpensOn(local.DayOfWeek);
        }

        return (time >= window.Start && OpensOn(local.DayOfWeek)) || (time < window.End && OpensOn(local.AddDays(-1).DayOfWeek));
    }
}

internal sealed record ModelPicture(DateTimeOffset At, IReadOnlyList<ModelPictureRow> Rows);

internal sealed record Sourced(string Name, DateTimeOffset AsOf, TimeSpan ValidFor)
{
    public static Sourced From(PluginFactSource source) => new(source.Name, source.AsOf, source.ValidFor);
}

internal sealed record ModelPriceFact(decimal? Input, decimal? Output, Sourced Source);

internal sealed record ModelPriceNow(decimal? Input, decimal? Output, IReadOnlyList<string> Applied);

internal sealed record ModelModifier(string Condition, decimal Factor, bool? ActiveNow);

internal sealed record ModelContextFact(long Tokens, Sourced Source);

internal sealed record ModelPeakWindow(string Origin, string Kind, TimeOnly Start, TimeOnly End, string TimeZoneId, IReadOnlyList<DayOfWeek> Days, bool ActiveNow, Sourced Source);

internal sealed record ModelPictureRow(
    string Profile,
    string Provider,
    string? Model,
    string? PublicModelRef,
    ModelPriceFact? Price,
    ModelPriceNow? PriceNow,
    IReadOnlyList<ModelModifier> Modifiers,
    ModelIndices? Indices,
    ModelContextFact? Context,
    IReadOnlyList<PluginLimit> Limits,
    IReadOnlyList<ModelPeakWindow> PeakWindows,
    IReadOnlyList<PluginQuotaState> Quota,
    Sourced? PublicSource,
    IReadOnlyList<ModelDerivedFact> Derived);

internal sealed record ModelDerivedFact(DerivedPattern Pattern, Sourced Source);
