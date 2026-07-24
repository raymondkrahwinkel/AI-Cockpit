using System.Collections.ObjectModel;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>
/// The usage-threshold settings for one provider (AC-233): every signal it declared, with the number the operator
/// put in place of its default, or nothing where they left it following.
/// <para>
/// A provider that declares no signals produces no rows, and its section is not shown at all — better than a frame
/// around controls that would do nothing.
/// </para>
/// </summary>
public sealed partial class UsageThresholdsViewModel : ObservableObject
{
    private readonly IUsageThresholdStore _store;
    private UsageThresholdSettings _settings = new();

    public UsageThresholdsViewModel(IUsageThresholdStore store)
    {
        _store = store;
    }

    /// <summary>One group per provider that reports anything, each with its own signals.</summary>
    public ObservableCollection<UsageThresholdProviderViewModel> Providers { get; } = [];

    /// <summary>Whether there is anything to show — false when no provider declares a usage signal.</summary>
    public bool HasProviders => Providers.Count > 0;

    /// <summary>
    /// Builds the rows from what the providers declared and what the operator has saved. Called when the settings
    /// screen opens, so a newly installed provider appears without a restart.
    /// </summary>
    public async Task LoadAsync(IReadOnlyList<(string ProviderId, string DisplayName, IReadOnlyList<PluginUsageSignal> Signals)> providers, CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        Providers.Clear();
        foreach (var (providerId, displayName, signals) in providers.Where(entry => entry.Signals.Count > 0))
        {
            var rows = signals.Select(signal => new UsageThresholdRowViewModel(
                signal.Key,
                signal.Label,
                signal.Description,
                signal.DefaultThresholdPercent,
                _Stored(providerId, signal.Key)));

            Providers.Add(new UsageThresholdProviderViewModel(providerId, displayName, [.. rows]));
        }

        OnPropertyChanged(nameof(HasProviders));
    }

    /// <summary>Persists every row: a number becomes an override, an empty field clears one so the provider's own default applies again.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in Providers)
        {
            foreach (var row in provider.Signals)
            {
                _settings.Set(_settings.ByProvider, provider.ProviderId, row.SignalKey, row.Threshold);
            }
        }

        await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The settings as they now stand, for handing to sessions started after the dialog closed.</summary>
    public async Task<UsageThresholdSettings> ReloadAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        return _settings;
    }

    /// <summary>What the operator saved for this provider's signal, or null where they left it following.</summary>
    private double? _Stored(string providerId, string signalKey) =>
        _settings.ByProvider.TryGetValue(providerId, out var signals) && signals.TryGetValue(signalKey, out var stored)
            ? stored
            : null;
}
