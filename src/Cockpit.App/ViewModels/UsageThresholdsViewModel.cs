using System.Collections.ObjectModel;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// AC-233 threshold settings for one provider; undeclared signals create no empty settings section.
public sealed partial class UsageThresholdsViewModel : ObservableObject
{
    private readonly IUsageThresholdStore _store;
    private UsageThresholdSettings _settings = new();

    public UsageThresholdsViewModel(IUsageThresholdStore store)
    {
        _store = store;
    }

    // One group per provider that reports anything, each with its own signals.
    public ObservableCollection<UsageThresholdProviderViewModel> Providers { get; } = [];

    // Whether there is anything to show — false when no provider declares a usage signal.
    public bool HasProviders => Providers.Count > 0;

    // The same groups again, but for what the Assistant warns at instead of an ordinary session (AC-805) — a
    // separate set of rows because the override hangs off the Assistant's role, not off whichever profile it
    // happens to be running on.
    public ObservableCollection<UsageThresholdProviderViewModel> AssistantProviders { get; } = [];

    public bool HasAssistantProviders => AssistantProviders.Count > 0;

    // Builds the rows from what the providers declared and what the operator has saved. Called when the settings
    // screen opens, so a newly installed provider appears without a restart.
    public async Task LoadAsync(IReadOnlyList<(string ProviderId, string DisplayName, IReadOnlyList<PluginUsageSignal> Signals)> providers, CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        Providers.Clear();
        AssistantProviders.Clear();
        foreach (var (providerId, displayName, signals) in providers.Where(entry => entry.Signals.Count > 0))
        {
            // The Assistant's own "what this follows" is what an ordinary session on this provider would resolve
            // to (its own override, else the declaration) — not always the raw declaration, now that a provider
            // override on the same screen can already change it.
            Providers.Add(_BuildGroup(providerId, displayName, signals, _settings.ByProvider, signal => signal.DefaultThresholdPercent));
            AssistantProviders.Add(_BuildGroup(providerId, displayName, signals, _settings.ByAssistant,
                signal => _settings.Resolve(providerId, profileLabel: null, signal.Key, signal.DefaultThresholdPercent, isAssistant: false)));
        }

        OnPropertyChanged(nameof(HasProviders));
        OnPropertyChanged(nameof(HasAssistantProviders));
    }

    private static UsageThresholdProviderViewModel _BuildGroup(
        string providerId,
        string displayName,
        IReadOnlyList<PluginUsageSignal> signals,
        Dictionary<string, Dictionary<string, double>> level,
        Func<PluginUsageSignal, double> fallback)
    {
        var rows = signals.Select(signal => new UsageThresholdRowViewModel(
            signal.Key,
            signal.Label,
            signal.Description,
            fallback(signal),
            _Stored(level, providerId, signal.Key)));

        return new UsageThresholdProviderViewModel(providerId, displayName, [.. rows]);
    }

    // Persists every row: a number becomes an override, an empty field clears one so the level above applies again.
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        _Save(Providers, _settings.ByProvider);
        _Save(AssistantProviders, _settings.ByAssistant);

        await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(true);
    }

    private void _Save(ObservableCollection<UsageThresholdProviderViewModel> groups, Dictionary<string, Dictionary<string, double>> level)
    {
        foreach (var provider in groups)
        {
            foreach (var row in provider.Signals)
            {
                _settings.Set(level, provider.ProviderId, row.SignalKey, row.Threshold);
            }
        }
    }

    // AC-999: the rows are already a buffer — nothing here reaches disk before `SaveAsync` — so undoing is a
    // matter of reading them back off the settings this view model was built from.
    public void Revert()
    {
        _Restore(Providers, _settings.ByProvider);
        _Restore(AssistantProviders, _settings.ByAssistant);
    }

    private static void _Restore(ObservableCollection<UsageThresholdProviderViewModel> groups, Dictionary<string, Dictionary<string, double>> level)
    {
        foreach (var provider in groups)
        {
            foreach (var row in provider.Signals)
            {
                row.Threshold = _Stored(level, provider.ProviderId, row.SignalKey);
            }
        }
    }

    // No override at any level is the default: every signal follows what its provider declared.
    public void RestoreDefaults()
    {
        foreach (var row in Providers.Concat(AssistantProviders).SelectMany(provider => provider.Signals))
        {
            row.Threshold = null;
        }
    }

    // The settings as they now stand, for handing to sessions started after the dialog closed.
    public async Task<UsageThresholdSettings> ReloadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        return _settings;
    }

    // What the operator saved for this provider's signal at the given level, or null where they left it following.
    private static double? _Stored(Dictionary<string, Dictionary<string, double>> level, string providerId, string signalKey) =>
        level.TryGetValue(providerId, out var signals) && signals.TryGetValue(signalKey, out var stored)
            ? stored
            : null;
}
