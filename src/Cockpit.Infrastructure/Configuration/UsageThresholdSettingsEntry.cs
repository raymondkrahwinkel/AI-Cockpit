namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of the operator's usage thresholds in the `usageThresholds` section of `cockpit.json`.
internal sealed class UsageThresholdSettingsEntry
{
    public Dictionary<string, Dictionary<string, double>> ByProvider { get; set; } = [];

    public Dictionary<string, Dictionary<string, double>> ByProfile { get; set; } = [];

    public Dictionary<string, Dictionary<string, double>> ByAssistant { get; set; } = [];
}
