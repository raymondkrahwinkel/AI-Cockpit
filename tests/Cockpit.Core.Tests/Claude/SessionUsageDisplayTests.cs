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
