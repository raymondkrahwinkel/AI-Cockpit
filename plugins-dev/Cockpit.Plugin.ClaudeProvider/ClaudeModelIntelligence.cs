using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

// The CLI exposes quota only while a session is alive. Keep that last honest reading for the optional provider
// contract instead of manufacturing a fresh-looking zero when no session has ever reported it.
internal sealed class ClaudeModelIntelligence
{
    private const string ProviderId = ClaudeProviderIds.Claude;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginQuotaState> _quota = new(StringComparer.Ordinal);

    public void ObserveWindows(
        IEnumerable<PluginRateLimitWindow> windows,
        string sourceName,
        DateTimeOffset asOf,
        Action<PluginModelObservation>? recordObservation)
    {
        var source = new PluginFactSource(sourceName, asOf, TimeSpan.Zero);
        foreach (var window in windows)
        {
            lock (_gate)
            {
                _quota[window.Label] = new PluginQuotaState(window, source);
            }

            recordObservation?.Invoke(new PluginModelObservation(ProviderId, PluginModelObservationKind.QuotaReading, asOf)
            {
                Quota = window,
            });
        }
    }

    public void ObserveStatusLine(
        IReadOnlyList<PluginUsageReading> readings,
        DateTimeOffset asOf,
        Action<PluginModelObservation>? recordObservation)
    {
        var windows = readings
            .Where(reading => reading.SignalKey is ClaudeUsageSignals.FiveHourKey or ClaudeUsageSignals.WeeklyKey)
            .Select(reading => new PluginRateLimitWindow(
                ClaudeUsageSignals.WindowLabel(reading.SignalKey == ClaudeUsageSignals.FiveHourKey
                    ? ClaudeUsageSignals.FiveHourWireType
                    : ClaudeUsageSignals.WeeklyWireType),
                reading.UsedPercent,
                reading.ResetsAt,
                WindowMinutes: null));
        ObserveWindows(windows, "Claude statusline", asOf, recordObservation);
    }

    public PluginModelIntelligence Read()
    {
        PluginQuotaState[] quota;
        lock (_gate)
        {
            quota = [.. _quota.Values.OrderBy(state => state.Window.Label, StringComparer.Ordinal)];
        }

        return new PluginModelIntelligence
        {
            // Claude aliases are deliberately mapped to the public ids; price itself remains null until the public
            // model picture supplies it. Fable has no public counterpart the plugin can name.
            Models =
            [
                new PluginModelCostEstimate("haiku") { PublicModelRef = "anthropic/claude-haiku-5.5" },
                new PluginModelCostEstimate("sonnet") { PublicModelRef = "anthropic/claude-sonnet-5.5" },
                new PluginModelCostEstimate("opus") { PublicModelRef = "anthropic/claude-opus-5.5" },
                new PluginModelCostEstimate("fable"),
            ],
            Quota = quota,
        };
    }
}
