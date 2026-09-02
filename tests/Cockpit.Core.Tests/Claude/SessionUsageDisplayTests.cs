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
    public void AFillLandsOnTheContextBar_AndAnAllowanceBecomesAWindow()
    {
        var session = Build();

        session.ApplyUsage(Signals,
        [
            new PluginUsageReading("context", 42.5, null),
            new PluginUsageReading("five-hour", 18.2, DateTimeOffset.Parse("2026-07-14T22:00:00Z")),
        ]);

        Assert.Equal(42.5, session.ContextUsedPercent);
        Assert.Single(session.RateLimits);
        Assert.Equal("5h", session.RateLimits[0].Label);
        Assert.Equal(18.2, session.RateLimits[0].UsedPercent);
    }

    [Fact]
    public void TheHoverText_SaysWhatTheBarsCannot_WhenEachWindowRollsOver()
    {
        var session = Build();

        session.ApplyUsage(Signals,
        [
            new PluginUsageReading("context", 42.5, null),
            new PluginUsageReading("five-hour", 18.2, DateTimeOffset.Parse("2026-07-14T22:00:00Z")),
            new PluginUsageReading("weekly", 7.4, DateTimeOffset.Parse("2026-07-20T00:00:00Z")),
        ]);

        // 42.5 rounds to 43, away from zero — .NET's default would say 42 and quietly under-report on the halves.
        Assert.Contains("Context window: 43% used", session.LimitsTooltip);
        Assert.Contains("Session (5 hours): 18% used — resets", session.LimitsTooltip);
        Assert.Contains("Week: 7% used — resets", session.LimitsTooltip);
    }

    [Fact]
    public void ASignalWithNoDescription_IsNamedByItsLabel()
    {
        var session = Build();
        var terse = new PluginUsageSignal("quota", "q", PluginUsageSignalKind.Allowance, 80);

        session.ApplyUsage([terse], [new PluginUsageReading("quota", 12, null)]);

        Assert.Equal("q: 12% used", session.LimitsTooltip);
    }

    [Fact]
    public void APartialSnapshot_LeavesTheOmittedSignalsShowingTheirLastKnownValue()
    {
        // AC-761 F1 / acceptance criterion 2: a snapshot with only ctx must not clear the two rate windows a
        // fuller snapshot already reported.
        var session = Build();

        session.ApplyUsage(Signals,
        [
            new PluginUsageReading("context", 20, null),
            new PluginUsageReading("five-hour", 18, DateTimeOffset.Parse("2026-07-14T22:00:00Z")),
            new PluginUsageReading("weekly", 7, DateTimeOffset.Parse("2026-07-20T00:00:00Z")),
        ]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 21, null)]);

        Assert.Equal(21, session.ContextUsedPercent);
        Assert.Equal(2, session.RateLimits.Count);
        Assert.Contains(session.RateLimits, w => w.Label == "5h" && w.UsedPercent == 18);
        Assert.Contains(session.RateLimits, w => w.Label == "wk" && w.UsedPercent == 7);
    }

    [Fact]
    public void AReadingForASignalNobodyDeclared_IsDropped()
    {
        // Guessing at an unknown key would put a mislabelled bar in the header. A renamed signal costs its bar
        // until the declaration catches up, which is the failure that shows rather than the one that misleads.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("something-else", 99, null)]);

        Assert.Null(session.ContextUsedPercent);
        Assert.Empty(session.RateLimits);
        Assert.Empty(session.LimitsTooltip);
    }

    [Fact]
    public void CrossingTheThreshold_RaisesTheBarOnce()
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
    }

    [Fact]
    public void DroppingBackAndClimbingAgain_SpeaksAgain()
    {
        // A compaction genuinely empties the context, so the next fill is news rather than a repeat.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 55, null)]);
        session.DismissUsageWarningCommand.Execute(null);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 12, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 51, null)]);

        Assert.True(session.HasUsageWarning);
    }

    [Fact]
    public void BelowTheThreshold_NothingIsSaid()
    {
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 49, null), new PluginUsageReading("weekly", 89, null)]);

        Assert.False(session.HasUsageWarning);
    }

    [Fact]
    public void AWarningAboutAnAllowance_SaysWhenItComesBack()
    {
        // The one thing a bar cannot show, and the thing you want most when it is nearly gone.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 95, DateTimeOffset.Now.AddHours(2))]);

        Assert.Contains("back", session.UsageWarning);
    }

    [Fact]
    public void AReadingBackUnderItsThreshold_TakesItsOwnWarningDownWithIt()
    {
        // A /clear empties the context and the very next reading says so, but the bar went on repeating the figure
        // from before it — a notice about a window that no longer existed, and only a click could remove it.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 55, null)]);
        Assert.True(session.HasUsageWarning);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.False(session.HasUsageWarning);
    }

    [Fact]
    public void AWarningThatClearedItself_SpeaksAgainOnTheNextCrossing()
    {
        // The same re-crossing as DroppingBackAndClimbingAgain, minus the dismiss that used to be needed in
        // between — the crossing is what speaks, and clearing on the way down must not have consumed it.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 55, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 51, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Context window is 51% used", session.UsageWarning);
    }

    [Fact]
    public void MultipleStandingWarnings_EachGetsItsOwnLine_AndTheRestSurviveOneClearing()
    {
        // AC-683: every signal that is over its own threshold gets its own line now, not one string a later
        // crossing overwrites — the week does not vanish from view because the context window crossed after it,
        // and it is still reported once context clears.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);

        Assert.Equal(2, session.Warnings.Count);
        Assert.Contains(session.Warnings, w => w.Text.Contains("Week is 95% used"));
        Assert.Contains(session.Warnings, w => w.Text.Contains("Context window is 60% used"));

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 95% used", session.UsageWarning);
    }

    [Fact]
    public void MultipleStandingWarnings_OrderOldestCrossingFirst_NotMostRecent()
    {
        // AC-683 criterion 9: the bar orders by severity and then the order each first crossed — not "last
        // crossed", which used to let a session that would still run happily bump the warning that will not.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.Equal(2, session.Warnings.Count);
        Assert.Contains("Week is 95% used", session.Warnings[0].Text);
        Assert.Contains("Session (5 hours) is 93% used", session.Warnings[1].Text);
    }

    [Fact]
    public void DismissingOneLine_LeavesTheOtherStandingWarningVisible()
    {
        // AC-683 criteria 8/10: a dismiss is a decision about one line, not the whole bar — silencing a subject
        // the operator never saw would be exactly the bug this collection replaces.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);

        session.DismissWarningCommand.Execute("weekly");

        Assert.True(session.HasUsageWarning, "the context line was never dismissed");
        Assert.Contains("Context window is 60% used", session.UsageWarning);

        // A third signal speaks and then goes quiet again: dismissing weekly must not have silenced it too.
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 4, null)]);

        Assert.True(session.HasUsageWarning, "the context line is still standing, untouched by the dismiss");
        Assert.Contains("Context window is 60% used", session.UsageWarning);
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

    [Fact]
    public void AFigureThatClimbsWhileItIsOnTheBar_IsKeptCurrent()
    {
        // The crossing is what speaks, but once the bar is up it should not be quoting a number from minutes ago.
        // Nothing reappears here — the bar was already showing this signal — so this is not the noise the
        // once-per-crossing rule exists to prevent.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 91, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 100, null)]);

        Assert.Contains("Week is 100% used", session.UsageWarning);
    }

    [Fact]
    public void AFigureThatClimbedWhileCoveredUp_ComesBackWithItsCurrentNumber()
    {
        // Keeping each standing signal's sentence is what lets a covered warning return at all, so that sentence
        // has to keep up. Frozen at the crossing, the week would come back saying 91% while sitting at 100 —
        // trading a bar that said nothing for one that understates exactly when it matters most.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 91, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 100, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.Contains("Session (5 hours) is 100% used", session.UsageWarning);
    }

    [Fact]
    public void AfterACompaction_TheContextFigureKeepsShowingItsLastKnownValue()
    {
        // AC-761 F1: Claude reports no context percentage right after a /compact — a snapshot that omits a
        // signal must not blank it out, since a known-but-stale figure beats no figure at all until the next
        // one lands (a session that never completes another turn used to show nothing here permanently).
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 88, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 20, null)]);

        Assert.Equal(88, session.ContextUsedPercent);
        Assert.Single(session.RateLimits);
    }
}
