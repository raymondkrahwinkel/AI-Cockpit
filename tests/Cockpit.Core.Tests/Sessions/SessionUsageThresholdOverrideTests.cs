using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// That a session actually judges its figures by the operator's threshold and not only by the provider's (AC-233).
/// The resolver is tested on its own; this is the wiring that carries it into the header and the warning.
/// </summary>
public class SessionUsageThresholdOverrideTests
{
    private static readonly PluginUsageSignal Weekly =
        new("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" };

    [Fact]
    public void WithAnOverride_TheWarningSpeaksAtTheOperatorsNumber()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 60);

        var session = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude" };

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 65, null)]);

        Assert.True(session.HasUsageWarning, "65% is past the 60 the operator set, though short of the provider's 90");
    }

    [Fact]
    public void WithAnOverride_TheBarColoursAtTheSameNumber()
    {
        // The whole point of one resolver: what warns and what colours cannot disagree.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProvider, "claude", "weekly", 60);

        var session = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude" };

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 65, null)]);

        var rateLimit = Assert.Single(session.RateLimits);
        Assert.Equal(60, rateLimit.ThresholdPercent);
    }

    [Fact]
    public void AProfileOverride_AppliesToThatSessionOnly()
    {
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByProfile, "work", "weekly", 50);

        var work = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude", ActiveProfileLabel = "work" };
        var personal = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude", ActiveProfileLabel = "personal" };

        work.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 55, null)]);
        personal.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 55, null)]);

        Assert.True(work.HasUsageWarning);
        Assert.False(personal.HasUsageWarning, "that profile still follows the provider's 90");
    }

    [Fact]
    public void WithNoSettingsLoaded_EverythingFollowsTheProvider()
    {
        var session = new TtyViewModel();

        session.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 95, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Equal(90, session.RateLimits[0].ThresholdPercent);
    }
}
