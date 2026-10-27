using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.ModelIntelligence;

namespace Cockpit.App.ViewModels;

// AC-1814: Options › Model intelligence. Refresh acts on the spot; the local table is staged and written on Apply.
public sealed partial class ModelIntelligenceOptionsViewModel(IModelIntelligenceOptions? source = null, TimeProvider? clock = null) : ObservableObject
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The table as last loaded or saved, so a pending change is whatever differs from it.
    private string _savedTable = string.Empty;

    public ObservableCollection<ModelSourceAge> Sources { get; } = [];

    public ObservableCollection<LocalModelRowViewModel> LocalModels { get; } = [];

    // Empty while the table is as saved; what the Options footer's pending-changes indicator compares.
    public string LocalTableFingerprint => _TableText() == _savedTable ? string.Empty : "changed:" + _TableText();

    private string _TableText() => string.Join("\n", LocalModels.Select(row => row.ToEntry()).Select(entry => $"{entry.ModelId}|{entry.Tier}|{string.Join(",", entry.Strengths)}|{entry.Notes}"));

    private void _AddRow(LocalModelEntry entry)
    {
        LocalModels.Add(new LocalModelRowViewModel(entry) { Changed = () => OnPropertyChanged(nameof(LocalTableFingerprint)) });
        OnPropertyChanged(nameof(LocalTableFingerprint));
    }

    public ObservableCollection<DerivedPatternRow> Patterns { get; } = [];

    public IReadOnlyList<ModelDataSource> Attribution => ModelDataAttribution.Sources;

    [ObservableProperty]
    private string _status = string.Empty;

    // Nothing derived yet is "unknown", said as such rather than as an empty list.
    public bool HasNoPatterns => Patterns.Count == 0;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null)
        {
            return;
        }

        _Show(await source.ReadAsync().ConfigureAwait(true), reloadTable: true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (source is null)
        {
            return;
        }

        var fetched = await source.RefreshAsync().ConfigureAwait(true);
        Status = fetched ? "Refreshed." : "The source did not answer; the last known data stays.";
        // A refresh leaves the staged table alone.
        _Show(await source.ReadAsync().ConfigureAwait(true), reloadTable: false);
    }

    [RelayCommand]
    private void AddLocalModel() => _AddRow(new LocalModelEntry(string.Empty, null, [], null));

    [RelayCommand]
    private void RemoveLocalModel(LocalModelRowViewModel row)
    {
        LocalModels.Remove(row);
        OnPropertyChanged(nameof(LocalTableFingerprint));
    }

    // The Options dialog's Apply.
    public async Task SaveStagedAsync()
    {
        if (source is null || LocalTableFingerprint.Length == 0)
        {
            return;
        }

        await source.SaveLocalModelsAsync([.. LocalModels.Select(row => row.ToEntry())]).ConfigureAwait(true);
        _savedTable = _TableText();
        OnPropertyChanged(nameof(LocalTableFingerprint));
    }

    [RelayCommand]
    private static void OpenSource(string url) => Cockpit.App.Services.ExternalLink.TryOpen(url);

    private void _Show(ModelIntelligenceOverview overview, bool reloadTable)
    {
        var now = _clock.GetUtcNow();
        Sources.Clear();
        foreach (var age in overview.Sources)
        {
            Sources.Add(new ModelSourceAge(age.Source, AgeText(age.AsOf, now)));
        }

        if (reloadTable)
        {
            LocalModels.Clear();
            foreach (var entry in overview.LocalModels)
            {
                _AddRow(entry);
            }

            _savedTable = _TableText();
            OnPropertyChanged(nameof(LocalTableFingerprint));
        }

        Patterns.Clear();
        foreach (var pattern in overview.Patterns)
        {
            Patterns.Add(PatternText(pattern));
        }

        OnPropertyChanged(nameof(HasNoPatterns));
    }

    internal static string AgeText(DateTimeOffset? asOf, DateTimeOffset now)
    {
        if (asOf is not { } at)
        {
            return "no data yet";
        }

        var age = now - at;
        return age.TotalMinutes < 1 ? "updated just now"
            : age.TotalHours < 1 ? $"updated {(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"updated {(int)age.TotalHours} h ago"
            : $"updated {(int)age.TotalDays} d ago";
    }

    internal static DerivedPatternRow PatternText(DerivedPattern pattern)
    {
        var (label, format) = pattern.Metric switch
        {
            ModelPatternMetric.QuotaDrainPercentPerHour => ("quota drain", (Func<double, string>)(value => $"{value:0.#}%/h")),
            ModelPatternMetric.LimitHitShare => ("limit hits", value => $"{value:P0}"),
            _ => ("latency", value => $"{value:0.#} s"),
        };
        var peaks = pattern.Peaks.Count == 0
            ? "no peak stands out"
            : string.Join("; ", pattern.Peaks.Select(peak =>
                $"{string.Join(", ", peak.Days.Select(day => day.ToString()[..3]))} {peak.Start:HH:mm}–{peak.End:HH:mm} ({format(peak.Value)} against {format(peak.Baseline)})"));
        return new DerivedPatternRow(
            $"{pattern.Provider}{(pattern.Model is null ? string.Empty : " / " + pattern.Model)} · {label}",
            $"{peaks} · {pattern.TimeZoneId}",
            string.Create(CultureInfo.CurrentCulture, $"n = {pattern.N} · confidence {pattern.Confidence:P0}"));
    }
}

public sealed record ModelSourceAge(string Name, string Age);

public sealed record DerivedPatternRow(string Title, string Peaks, string Basis);

public sealed partial class LocalModelRowViewModel(LocalModelEntry entry) : ObservableObject
{
    [ObservableProperty]
    private string _modelId = entry.ModelId;

    [ObservableProperty]
    private string? _tier = entry.Tier;

    // Comma-separated while it is edited.
    [ObservableProperty]
    private string _strengthsText = string.Join(", ", entry.Strengths);

    [ObservableProperty]
    private string? _notes = entry.Notes;

    internal Action? Changed { get; init; }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Changed?.Invoke();
    }

    internal LocalModelEntry ToEntry() => new(
        ModelId.Trim(),
        string.IsNullOrWhiteSpace(Tier) ? null : Tier.Trim(),
        [.. StrengthsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)],
        string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
}
