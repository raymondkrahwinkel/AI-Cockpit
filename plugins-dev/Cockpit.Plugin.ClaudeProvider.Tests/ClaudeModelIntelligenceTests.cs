using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

public class ClaudeModelIntelligenceTests
{
    [Fact]
    public void Read_KeepsEachProfilesQuotaApartAndNeverLendsOneToAnother()
    {
        var observations = new List<PluginModelObservation>();
        var intelligence = new ClaudeModelIntelligence();
        var keyA = ClaudeConfigPaths.ProfileKey(Path.Combine(Path.GetTempPath(), "profile-a"), Path.GetTempPath());
        var keyB = ClaudeConfigPaths.ProfileKey(Path.Combine(Path.GetTempPath(), "profile-b"), Path.GetTempPath());
        var sdkUsageB = new ClaudeSdkUsage(intelligence, observations.Add, keyB);
        PluginRateLimitWindow Window(double used) => new("5h", used, null, null);

        intelligence.ObserveWindows([Window(10)], "test", DateTimeOffset.UtcNow, observations.Add, profile: keyA);
        using (var document = JsonDocument.Parse("""
            {"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour","utilization":1,"resetsAt":1785574800}}
            """))
        {
            sdkUsageB.Observe(document.RootElement);
        }
        intelligence.ObserveWindows([Window(20)], "test", DateTimeOffset.UtcNow, observations.Add, profile: keyA);

        Assert.NotEqual(keyA, keyB);
        Assert.Equal(20d, Assert.Single(intelligence.Read(keyA).Quota).Window.UsedPercent);
        Assert.Equal(keyA, intelligence.Read(keyA).Profile);
        Assert.Equal(100d, Assert.Single(intelligence.Read(keyB).Quota).Window.UsedPercent, precision: 10);
        Assert.Empty(intelligence.Read(Path.Combine(Path.GetTempPath(), "never-seen")).Quota);
        Assert.Equal(keyB, Assert.Single(observations, observation => observation.Kind == PluginModelObservationKind.RateLimitRefusal).Profile);
        Assert.Equal([keyA, keyB, keyA], observations.Where(observation => observation.Kind == PluginModelObservationKind.QuotaReading).Select(observation => observation.Profile));
    }
}
