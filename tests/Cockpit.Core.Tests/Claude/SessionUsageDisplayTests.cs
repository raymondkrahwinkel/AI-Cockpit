using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// How a provider's usage readings reach the session header (AC-229). The host matches each reading to the signal
/// that declared it and renders what it is told — it knows a fill from an allowance and nothing else, so a
/// provider can report windows this code has never heard of.
/// </summary>
public class SessionUsageDisplayTests
{
    private static readonly PluginUsageSignal Context =
        new("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" };

    private static readonly PluginUsageSignal FiveHour =
        new("five-hour", "5h", PluginUsageSignalKind.Allowance, 90) { Description = "Session (5 hours)" };

    private static readonly PluginUsageSignal Weekly =
        new("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" };

    private static readonly IReadOnlyList<PluginUsageSignal> Signals = [Context, FiveHour, Weekly];

    private static TtyViewModel Build() => new();

    [Fact]
    public void APartialSnapshot_LeavesTheOmittedSignalsShowingTheirLastKnownValue()
    {
        // AC-761 F1 / criterion 2, both directions at once: Claude reports no context percentage right after a
        // /compact and no rate windows on an ordinary turn, so an omitted signal must keep its last known figure
        // rather than blank out — a session that never completes another turn used to show nothing, permanently.
        var session = Build();

        session.ApplyUsage(Signals,
        [
            new PluginUsageReading("context", 88, null),
            new PluginUsageReading("five-hour", 18, DateTimeOffset.Parse("2026-07-14T22:00:00Z")),
            new PluginUsageReading("weekly", 7, DateTimeOffset.Parse("2026-07-20T00:00:00Z")),
        ]);

        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 20, DateTimeOffset.Parse("2026-07-14T22:00:00Z"))]);

        Assert.Equal(88, session.ContextUsedPercent);
        Assert.Equal(2, session.RateLimits.Count);
        Assert.Contains(session.RateLimits, w => w.Label == "5h" && w.UsedPercent == 20);
        Assert.Contains(session.RateLimits, w => w.Label == "wk" && w.UsedPercent == 7);
    }

    [Fact]
    public void CrossingTheThreshold_RaisesTheBarOnce_UntilItHasDroppedBack()
    {
        // Every poll re-reads the same file. A bar that reappears at 91%, 92%, 93% is noise, and noise gets
        // ignored exactly when it matters — so the crossing speaks, not the state.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 91, null)]);
        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 91% used", session.UsageWarning);

        session.DismissUsageWarningCommand.Execute(null);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 92, null)]);

        Assert.False(session.HasUsageWarning, "the same crossing has already been announced");

        // Dropping back and climbing again is news rather than a repeat (a compaction genuinely empties the context).
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 12, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 93, null)]);

        Assert.True(session.HasUsageWarning);
    }

    [Fact]
    public void ADismissedSignalThatGoesAwayAndComesBack_IsNewsAgain_WithoutDisturbingItsSiblings()
    {
        // Silence lasts until the figure has actually been away, and a dismiss is a decision about one line — a
        // sibling that was never dismissed keeps reporting throughout, and a third signal's own cycle up and back
        // down again does not interfere either.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        session.DismissWarningCommand.Execute("weekly");

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 12, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 96, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 4, null)]);

        Assert.Equal(2, session.Warnings.Count);
        Assert.Contains("Context window is 60% used", session.Warnings[0].Text);
        Assert.Contains("Week is 96% used", session.Warnings[1].Text);
    }
}
