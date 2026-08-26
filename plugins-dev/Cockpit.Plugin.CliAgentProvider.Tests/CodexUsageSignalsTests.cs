using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// #1105 criterion 6: the driver's window label and the weekly signal's declared Label must never drift apart —
// a drift would make the declared-vs-window match silently fail, the exact bug this ticket fixes.
public class CodexUsageSignalsTests
{
    [Fact]
    public void TheWeeklyDeclarationsLabel_MatchesWhatTheDriverProducesForTheSameSpan()
    {
        Assert.Equal("7d", CodexUsageSignals.WindowLabel(CodexUsageSignals.WeeklyWindowMinutes));

        var weekly = Assert.Single(CodexUsageSignals.Declarations, signal => signal.Key == CodexUsageSignals.WeeklyKey);
        Assert.Equal(CodexUsageSignals.WindowLabel(CodexUsageSignals.WeeklyWindowMinutes), weekly.Label);
    }

    [Fact]
    public void TheWeeklySignal_SupportsResume_WithTheResetTimeCarriedByTheReading()
    {
        // #1105 decision 4: resume is offered, since a Codex reset is up to seven days out rather than a few
        // hours — the resettable moment itself rides PluginUsageReading.ResetsAt (SessionPanelViewModel already
        // renders it), so this only pins that the signal opts in.
        var weekly = Assert.Single(CodexUsageSignals.Declarations, signal => signal.Key == CodexUsageSignals.WeeklyKey);

        Assert.True(weekly.SupportsResume);
        Assert.Equal(PluginUsageSignalKind.Allowance, weekly.Kind);
    }

    [Fact]
    public void TheWeeklyThreshold_Defaults75Percent_LowerThanClaudesNinety()
    {
        // #1105 decision 2: a seven-day window with no credit fallback needs to warn early enough to spread the
        // remaining budget, not just to finish up — still just a default, overridable in Options like any signal.
        var weekly = Assert.Single(CodexUsageSignals.Declarations, signal => signal.Key == CodexUsageSignals.WeeklyKey);

        Assert.Equal(75, weekly.DefaultThresholdPercent);
    }
}
