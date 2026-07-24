namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of the operator's usage thresholds in the <c>usageThresholds</c> section of <c>cockpit.json</c>.</summary>
internal sealed class UsageThresholdSettingsEntry
{
    public Dictionary<string, Dictionary<string, double>> ByProvider { get; set; } = [];

    public Dictionary<string, Dictionary<string, double>> ByProfile { get; set; } = [];
}
