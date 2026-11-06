using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;
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
    public void AProfileOrAssistantOverride_AppliesToThatSessionOnly()
    {
        var byProfile = new UsageThresholdSettings();
        byProfile.Set(byProfile.ByProfile, "work", "weekly", 50);

        var work = new TtyViewModel { UsageThresholds = byProfile, UsageProviderId = "claude", ActiveProfileLabel = "work" };
        var personal = new TtyViewModel { UsageThresholds = byProfile, UsageProviderId = "claude", ActiveProfileLabel = "personal" };

        work.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 55, null)]);
        personal.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 55, null)]);

        Assert.True(work.HasUsageWarning);
        Assert.False(personal.HasUsageWarning, "that profile still follows the provider's 90");

        // AC-805: the same profile ("work" here) serves both the Assistant and an ordinary session — the
        // Assistant's own threshold must not leak onto the ordinary session that happens to share it.
        var settings = new UsageThresholdSettings();
        settings.Set(settings.ByAssistant, "claude", "weekly", 25);

        var assistant = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude", ActiveProfileLabel = "work" };
        assistant.AdoptPaneId(AssistantIdentity.PaneId);
        var ordinary = new TtyViewModel { UsageThresholds = settings, UsageProviderId = "claude", ActiveProfileLabel = "work" };

        assistant.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 30, null)]);
        ordinary.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 30, null)]);

        Assert.True(assistant.HasUsageWarning, "30% is past the Assistant's own 25%");
        Assert.False(ordinary.HasUsageWarning, "the ordinary session on the same profile still follows the provider's 90");
    }
}
