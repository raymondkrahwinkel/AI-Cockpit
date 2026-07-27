using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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

        session.ContextUsedPercent.Should().Be(42.5);
        session.RateLimits.Should().ContainSingle();
        session.RateLimits[0].Label.Should().Be("5h");
        session.RateLimits[0].UsedPercent.Should().Be(18.2);
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
        session.LimitsTooltip.Should().Contain("Context window: 43% used");
        session.LimitsTooltip.Should().Contain("Session (5 hours): 18% used — resets");
        session.LimitsTooltip.Should().Contain("Week: 7% used — resets");
    }

    [Fact]
    public void ASignalWithNoDescription_IsNamedByItsLabel()
    {
        var session = Build();
        var terse = new PluginUsageSignal("quota", "q", PluginUsageSignalKind.Allowance, 80);

        session.ApplyUsage([terse], [new PluginUsageReading("quota", 12, null)]);

        session.LimitsTooltip.Should().Be("q: 12% used");
    }

    [Fact]
    public void AReadingForASignalNobodyDeclared_IsDropped()
    {
        // Guessing at an unknown key would put a mislabelled bar in the header. A renamed signal costs its bar
        // until the declaration catches up, which is the failure that shows rather than the one that misleads.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("something-else", 99, null)]);

        session.ContextUsedPercent.Should().BeNull();
        session.RateLimits.Should().BeEmpty();
        session.LimitsTooltip.Should().BeEmpty();
    }

    [Fact]
    public void CrossingTheThreshold_RaisesTheBarOnce()
    {
        // Every poll re-reads the same file. A bar that reappears at 91%, 92%, 93% is noise, and noise gets
        // ignored exactly when it matters — so the crossing speaks, not the state.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 91, null)]);
        session.HasUsageWarning.Should().BeTrue();
        session.UsageWarning.Should().Contain("Week is 91% used");

        session.DismissUsageWarningCommand.Execute(null);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 92, null)]);

        session.HasUsageWarning.Should().BeFalse("the same crossing has already been announced");
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

        session.HasUsageWarning.Should().BeTrue();
    }

    [Fact]
    public void BelowTheThreshold_NothingIsSaid()
    {
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 49, null), new PluginUsageReading("weekly", 89, null)]);

        session.HasUsageWarning.Should().BeFalse();
    }

    [Fact]
    public void AWarningAboutAnAllowance_SaysWhenItComesBack()
    {
        // The one thing a bar cannot show, and the thing you want most when it is nearly gone.
        var session = Build();

        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 95, DateTimeOffset.Now.AddHours(2))]);

        session.UsageWarning.Should().Contain("back");
    }

    // The ten below assert with xunit's own Assert rather than the FluentAssertions the rest of this file uses:
    // that package is commercially licensed from v8 and is on its way out of the codebase (AC-372). Adding to it
    // here would only make that sweep bigger.

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
    public void ASignalGoingQuiet_LeavesAnotherSignalsWarningStanding()
    {
        // One string carries all of them, so a context bar dropping to nothing must not wipe a week that is nearly
        // spent. That is the warning you would most want kept and the one you are least likely to be watching for.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 95% used", session.UsageWarning);
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
    public void AWarningACrossingCoveredUp_ComesBackWhenTheCoverGoesQuiet()
    {
        // One string carries every signal, so a later crossing writes over an earlier one. The earlier figure has
        // spent its crossing by then, so when the cover cleared it used to leave an empty bar and never speak
        // again — the week stayed at 95% with nothing on screen saying so.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        Assert.Contains("Context window is 60% used", session.UsageWarning);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 95% used", session.UsageWarning);
    }

    [Fact]
    public void TheBarGoesBackToTheMostRecentCrossing_NotTheOldestOneStillStanding()
    {
        // Which of several standing warnings gets the bar is the same rule that put it there to begin with: the
        // newest crossing. Anything else would mean a bar clearing quietly promotes an older figure over a newer
        // one, and the host has no ranking of its own to justify that — a provider's signals are its business.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("context", 4, null)]);

        Assert.Contains("Session (5 hours) is 93% used", session.UsageWarning);
    }

    [Fact]
    public void DismissingTheBar_SilencesWhatItWasCoveringUpAsWell()
    {
        // Dismiss is a decision about the bar, not about the sentence that happened to be in it. Silencing only
        // the words on screen would leave the covered warning free to appear later, on the back of some third
        // signal clearing — a bar that comes back on its own after a click reads as the click not having worked.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        session.DismissUsageWarningCommand.Execute(null);
        Assert.False(session.HasUsageWarning);

        // A third signal speaks and then goes quiet again: the bar has somewhere to fall back to, and must not.
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 4, null)]);

        Assert.False(session.HasUsageWarning, "the week went quiet along with the bar it was standing under");
    }

    [Fact]
    public void ASilencedSignalThatGoesAwayAndComesBack_IsNewsAgain()
    {
        // Silence lasts until the figure has actually been away. Otherwise dismissing once would mute that signal
        // for the life of the session, and the next genuine crossing — the one you would want — says nothing.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        session.DismissUsageWarningCommand.Execute(null);

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 12, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 96, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 96% used", session.UsageWarning);
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
    public void ASignalThatCameBackAfterBeingSilenced_CanHoldTheBarAgain()
    {
        // Being away lifts the silence for the fallback too, not only for speaking. Kept, a spent silence would
        // skip that signal for the rest of the session — it would say its piece on the crossing and then never
        // be the one the bar falls back to, which is the swallowing this whole change is about.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 95, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 60, null)]);
        session.DismissUsageWarningCommand.Execute(null);

        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 12, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("weekly", 96, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 93, null)]);
        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 4, null)]);

        Assert.True(session.HasUsageWarning);
        Assert.Contains("Week is 96% used", session.UsageWarning);
    }

    [Fact]
    public void AfterACompaction_TheContextFigureGoesBackToSilence()
    {
        // Claude reports no context percentage right after a /compact. The bar must go quiet rather than keep
        // showing the number from before, which would be a claim about a window that just emptied.
        var session = Build();
        session.ApplyUsage(Signals, [new PluginUsageReading("context", 88, null)]);

        session.ApplyUsage(Signals, [new PluginUsageReading("five-hour", 20, null)]);

        session.ContextUsedPercent.Should().BeNull();
        session.RateLimits.Should().ContainSingle();
    }
}
